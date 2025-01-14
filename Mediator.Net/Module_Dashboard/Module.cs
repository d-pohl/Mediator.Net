﻿// Licensed to ifak e.V. under one or more agreements.
// ifak e.V. licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Ifak.Fast.Mediator.Util;
using Ifak.Fast.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.DependencyInjection;

namespace Ifak.Fast.Mediator.Dashboard
{
    public class Module : ModelObjectModule<DashboardModel>
    {
        private string absolutBaseDir = "";
        private readonly string BundlesPrefix = Guid.NewGuid().ToString().Replace("-", "");
        private int clientPort;
        private ViewType[] viewTypes = Array.Empty<ViewType>();
        private DashboardUI uiModel = new DashboardUI();

        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();

        private static SynchronizationContext? theSyncContext = null;
        private WebApplication? webHost = null;
        private bool isRunning = false;

        public override IModelObject? UnnestConfig(IModelObject parent, object? obj) {
            if (obj is DataValue dv && parent is View view) {
                string type = view.Type;
                ViewType? viewType = viewTypes.FirstOrDefault(vt => vt.Name == type);
                if (viewType != null && viewType.ConfigType != null) {
                    Type t = viewType.ConfigType;
                    object? configObj = dv.Object(t);
                    if (configObj is IModelObject mob) {
                        return mob;
                    }
                }
            }
            return null;
        }

        public override async Task Init(ModuleInitInfo info,
                                        VariableValue[] restoreVariableValues,
                                        Notifier notifier,
                                        ModuleThread moduleThread) {

            theSyncContext = SynchronizationContext.Current;

            await base.Init(info, restoreVariableValues, notifier, moduleThread);

            var config = info.GetConfigReader();

            clientPort = info.LoginPort;

            string baseDir = config.GetString("base-dir");
            string host = config.GetString("listen-host");
            int port = config.GetInt("listen-port");

            string strViewAssemblies = config.GetString("view-assemblies");

            const string releaseDebugPlaceHolder = "{RELEASE_OR_DEBUG}";
            if (strViewAssemblies.Contains(releaseDebugPlaceHolder)) {
#if DEBUG
                strViewAssemblies = strViewAssemblies.Replace(releaseDebugPlaceHolder, "Debug");
#else
                strViewAssemblies = strViewAssemblies.Replace(releaseDebugPlaceHolder, "Release");
#endif
            }

            string[] viewAssemblies = strViewAssemblies
                .Split(new char[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToArray();

            absolutBaseDir = Path.GetFullPath(baseDir);
            if (!Directory.Exists(absolutBaseDir)) throw new Exception($"base-dir does not exist: {absolutBaseDir}");

            string[] absoluteViewAssemblies = viewAssemblies.Select(d => Path.GetFullPath(d)).ToArray();
            foreach (string dir in absoluteViewAssemblies) {
                if (!File.Exists(dir)) throw new Exception($"view-assembly does not exist: {dir}");
            }

            viewTypes = ReadAvailableViewTypes(absolutBaseDir, BundlesPrefix, absoluteViewAssemblies);
            uiModel = MakeUiModel(model, viewTypes);

            await base.OnConfigModelChanged(init: false); // required for UnnestConfig to work (viewTypes need to be loaded)


            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
                ContentRootPath = Directory.GetCurrentDirectory(),
                WebRootPath = absolutBaseDir
            });
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddCors();

            WebApplication app = builder.Build();
            app.Urls.Add($"http://{host}:{port}");

            var webSocketOptions = new WebSocketOptions() {
                KeepAliveInterval = TimeSpan.FromSeconds(60)
            };
            app.UseWebSockets(webSocketOptions);

            string regex = $"^{BundlesPrefix}/(.*)";
            var rewriteOptions = new RewriteOptions().AddRewrite(regex, "/$1", skipRemainingRules: true);
            app.UseRewriter(rewriteOptions);

            app.Use(async (context, nextMiddleware) => {
                string path = context.Request.Path;
                if (path == "/") {
                    context.Response.OnStarting(() => {
                        context.Response.Headers.Add("Expires", (DateTime.UtcNow + TimeSpan.FromMinutes(1)).ToString("r"));
                        return Task.CompletedTask;
                    });
                }
                await nextMiddleware();
            });

            var options = new DefaultFilesOptions();
            options.DefaultFileNames.Clear();
            options.DefaultFileNames.Add("App/index.html");
            app.UseDefaultFiles(options);

            app.UseStaticFiles();

            app.UseCors(builder => {
                builder.WithOrigins("http://localhost:8080").AllowAnyMethod().AllowAnyHeader();
            });

            app.Run((context) => {
                //logger.Info($"HTTP {context.Request.Path} {Thread.CurrentThread.ManagedThreadId}");
                var promise = new TaskCompletionSource<bool>();
                theSyncContext!.Post(_ => {
                    Task task = HandleClientRequest(context);
                    task.ContinueWith(completedTask => promise.CompleteFromTask(completedTask));
                }, null);
                return promise.Task;
            });

            Task _ = app.StartAsync();
        }

        public async override Task InitAbort() {
            try {
                await webHost!.StopAsync();
            }
            catch (Exception) { }
        }
        
        public override async Task Run(Func<bool> fShutdown) {

            await Task.Delay(1000);

            isRunning = true;

            while (!fShutdown()) {

                bool needPurge = sessions.Values.Any(session => session.IsAbandoned);
                if (needPurge) {
                    var sessionItems = sessions.Values.ToList();
                    foreach (var session in sessionItems) {
                        if (session.IsAbandoned) {
                            Console.WriteLine("Closing abandoned session: " + session.ID);
                            var ignored2 = session.Close();
                            sessions.Remove(session.ID);
                        }
                    }
                }

                await Task.Delay(1000);
            }

            isRunning = false;

            Task closeTask = Task.WhenAll(sessions.Values.Select(session => session.Close()).ToArray());
            await Task.WhenAny(closeTask, Task.Delay(2000));

            if (webHost != null) {
                _ = webHost.StopAsync();
            }
        }

        private async Task HandleClientWebSocket(WebSocket socket) {

            try {

                const int maxMessageSize = 1024;
                byte[] receiveBuffer = new byte[maxMessageSize];

                WebSocketReceiveResult receiveResult = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), CancellationToken.None);

                if (receiveResult.MessageType == WebSocketMessageType.Close) {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
                else if (receiveResult.MessageType == WebSocketMessageType.Binary) {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Cannot accept binary frame", CancellationToken.None);
                }
                else {

                    int count = receiveResult.Count;

                    while (!receiveResult.EndOfMessage) {

                        if (count >= maxMessageSize) {
                            string closeMessage = string.Format("Maximum message size: {0} bytes.", maxMessageSize);
                            await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, closeMessage, CancellationToken.None);
                            return;
                        }
                        receiveResult = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer, count, maxMessageSize - count), CancellationToken.None);
                        count += receiveResult.Count;
                    }

                    var sessionID = Encoding.UTF8.GetString(receiveBuffer, 0, count);

                    if (!sessions.ContainsKey(sessionID)) {
                        Task ignored = socket.CloseAsync(WebSocketCloseStatus.ProtocolError, string.Empty, CancellationToken.None);
                        throw new InvalidSessionException();
                    }

                    Session session = sessions[sessionID];

                    await session.ReadWebSocket(socket);
                }
            }
            catch (Exception exp) {
                if (exp is not InvalidSessionException) {
                    Exception e = exp.GetBaseException() ?? exp;
                    logWarn("Error handling web socket request: " + e.Message);
                }
            }
        }

        private async Task HandleClientRequest(HttpContext context) {

            HttpRequest request = context.Request;
            HttpResponse response = context.Response;

            if (!isRunning) {
                response.StatusCode = 400; // BAD Request
                return;
            }

            try {

                if (request.Path == "/websocket/" && context.WebSockets.IsWebSocketRequest) {
                    WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await HandleClientWebSocket(webSocket);
                    return;
                }

                switch (request.Method) {

                    case "POST":

                        using (ReqResult result = await HandlePost(request, response)) {
                            response.StatusCode = result.StatusCode;
                            response.ContentLength = result.Bytes.Length;
                            response.ContentType = result.ContentType;
                            try {
                                await result.Bytes.CopyToAsync(response.Body);
                            }
                            catch (Exception) { }
                        }
                        return;

                    default:

                        response.StatusCode = 400;
                        return;
                }
            }
            catch (Exception exp) {
                response.StatusCode = 500;
                logWarn("Error handling client request", exp);
            }
        }

        private const string Path_Login = "/login";
        private const string Path_Logout = "/logout";
        private const string Path_ViewReq = "/viewRequest/";
        private const string Path_ActivateView = "/activateView";
        private const string Path_DuplicateView = "/duplicateView";
        private const string Path_DuplicateConvertView = "/duplicateConvertView";
        private const string Path_RenameView = "/renameView";
        private const string Path_MoveView = "/moveView";
        private const string Path_DeleteView = "/deleteView";

        private async Task<ReqResult> HandlePost(HttpRequest request, HttpResponse response) {

            string path = request.Path;

            try {

                if (path == Path_Login) {

                    string? user;
                    string? pass;
                    using (var reader = new StreamReader(request.Body, Encoding.UTF8)) {
                        var obj = await StdJson.JObjectFromReaderAsync(reader);
                        user = (string?)obj["user"];
                        pass = (string?)obj["pass"];
                        if (user == null || pass == null) {
                            return ReqResult.Bad("Missing user and password.");
                        }
                    }

                    var session = new Session();
                    Connection connection;
                    try {
                        connection = await HttpConnection.ConnectWithUserLogin("localhost", clientPort, user, pass, null, session, timeoutSeconds: 90);
                    }
                    catch (Exception exp) {
                        logWarn(exp.Message);
                        return ReqResult.Bad(exp.Message);
                    }
                    await session.SetConnection(connection, model, moduleID, viewTypes);
                    sessions[session.ID] = session;

                    var result = new JObject();
                    result["sessionID"] = session.ID;
                    string str = StdJson.ObjectToString(uiModel);
                    JRaw raw = new JRaw(str);
                    result["model"] = raw;
                    return ReqResult.OK(result);
                }
                else if (path.StartsWith(Path_ViewReq)) {

                    string viewRequest = path.Substring(Path_ViewReq.Length);

                    (Session session, string viewID) = GetSessionFromQuery(request.QueryString.ToString());

                    string content;
                    using (var reader = new StreamReader(request.Body, Encoding.UTF8)) {
                        content = await reader.ReadToEndAsync();
                    }
                    return await session.OnViewCommand(viewID, viewRequest, DataValue.FromJSON(content));
                }
                else if (path == Path_ActivateView) {

                    (Session session, string viewID) = GetSessionFromQuery(request.QueryString.ToString());
                    await session.OnActivateView(viewID);
                    return ReqResult.OK();
                }
                else if (path == Path_DuplicateView) {

                    (Session session, string viewID) = GetSessionFromQuery(request.QueryString.ToString());
                    string newViewID = await session.OnDuplicateView(viewID);

                    uiModel = MakeUiModel(model, viewTypes);

                    return ReqResult.OK(new {
                        newViewID,
                        model = uiModel
                    });
                }
                else if (path == Path_DuplicateConvertView) {

                    (Session session, string viewID) = GetSessionFromQuery(request.QueryString.ToString());
                    string newViewID = await session.OnDuplicateConvertHistoryPlot(viewID);

                    uiModel = MakeUiModel(model, viewTypes);

                    return ReqResult.OK(new {
                        newViewID,
                        model = uiModel
                    });
                }
                else if (path == Path_RenameView) {

                    (Session session, string viewID) = GetSessionFromQuery(request.QueryString.ToString());

                    string? newViewName;
                    using (var reader = new StreamReader(request.Body, Encoding.UTF8)) {
                        var obj = await StdJson.JObjectFromReaderAsync(reader);
                        newViewName = (string?)obj["newViewName"];
                        if (newViewName == null) {
                            return ReqResult.Bad("Missing newViewName");
                        }
                    }

                    await session.OnRenameView(viewID, newViewName);

                    uiModel = MakeUiModel(model, viewTypes);

                    return ReqResult.OK(new {
                        model = uiModel
                    });
                }
                else if (path == Path_MoveView) {

                    (Session session, string viewID) = GetSessionFromQuery(request.QueryString.ToString());

                    bool up = false;
                    using (var reader = new StreamReader(request.Body, Encoding.UTF8)) {
                        var obj = await StdJson.JObjectFromReaderAsync(reader);
                        up = (bool)obj["up"]!;
                    }

                    await session.OnMoveView(viewID, up);

                    uiModel = MakeUiModel(model, viewTypes);

                    return ReqResult.OK(new {
                        model = uiModel
                    });
                }
                else if (path == Path_DeleteView) {

                    (Session session, string viewID) = GetSessionFromQuery(request.QueryString.ToString());
                    await session.OnDeleteView(viewID);

                    uiModel = MakeUiModel(model, viewTypes);

                    return ReqResult.OK(new {
                        model = uiModel
                    });
                }
                else if (path == Path_Logout) {

                    string sessionID;
                    using (var reader = new StreamReader(request.Body, Encoding.UTF8)) {
                        sessionID = await reader.ReadToEndAsync();
                    }

                    if (sessions.ContainsKey(sessionID)) {
                        Session session = sessions[sessionID];
                        var ignored = session.Close();
                        sessions.Remove(sessionID);
                    }

                    return ReqResult.OK();
                }
                else {
                    return ReqResult.Bad("Invalid path: " + path);
                }
            }
            catch (InvalidSessionException exp) {
                logWarn("HandlePost: " + exp.Message);
                return ReqResult.Bad(exp.Message);
            }
            catch (Exception exp) {
                logWarn("HandlePost:", exp);
                return ReqResult.Bad(exp.Message);
            }
        }

        private (Session sesssion, string viewID) GetSessionFromQuery(string query) {
            int i = query.IndexOf('_');
            if (i <= 0) {
                throw new Exception("Invalid context");
            }
            string sessionID = query.Substring(1, i - 1);
            string viewID = query.Substring(i + 1);

            if (!sessions.ContainsKey(sessionID)) {
                throw new InvalidSessionException();
            }
            Session session = sessions[sessionID];
            return (session, viewID);
        }

        private static ViewType[] ReadAvailableViewTypes(string absoluteBaseDir, string bundlesPrefx, string[] viewAssemblies) {

            var viewTypes = Reflect.GetAllNonAbstractSubclasses(typeof(ViewBase)).ToList();
            viewTypes.AddRange(viewAssemblies.SelectMany(LoadTypesFromAssemblyFile));

            var result = new List<ViewType>();

            foreach (Type type in viewTypes) {
                Identify? id = type.GetCustomAttribute<Identify>();
                if (id != null) {
                    string viewBundle = "ViewBundle_" + id.Bundle;
                    string viewBundlePath = Path.Combine(absoluteBaseDir, viewBundle);
                    bool url = type == typeof(View_ExtURL);
                    if (url || Directory.Exists(viewBundlePath)) {
                        var vt = new ViewType() {
                            Name = id.ID,
                            HtmlPath = $"/{bundlesPrefx}/" + viewBundle + "/" + id.Path, // "/" + dir.Name + "/" + indexFile,
                            Type = type,
                            ConfigType = id.ConfigType,
                            Icon = id.Icon ?? ""
                        };
                        result.Add(vt);
                    }
                    else {
                        logWarn($"No ViewBundle folder found for View {id.ID} in {absoluteBaseDir}");
                    }
                }
            }
            return result.ToArray();
        }

        private static Type[] LoadTypesFromAssemblyFile(string fileName) {
            try {
                Type baseClass = typeof(ViewBase);

                var loader = McMaster.NETCore.Plugins.PluginLoader.CreateFromAssemblyFile(
                        fileName,
                        sharedTypes: new Type[] { baseClass });

                return loader.LoadDefaultAssembly()
                    .GetExportedTypes()
                    .Where(t => t.IsSubclassOf(baseClass) && !t.IsAbstract)
                    .ToArray();
            }
            catch (Exception exp) {
                Console.Error.WriteLine($"Failed to load view types from assembly '{fileName}': {exp.Message}");
                Console.Error.Flush();
                return new Type[0];
            }
        }

        private static DashboardUI MakeUiModel(DashboardModel model, ViewType[] viewTypes) {

            DashboardUI result = new DashboardUI();

            foreach (View v in model.Views) {

                ViewType? viewType = viewTypes.FirstOrDefault(vt => vt.Name.Equals(v.Type, StringComparison.InvariantCultureIgnoreCase));
                if (viewType == null) throw new Exception($"No view type '{v.Type}' found!");

                bool url = viewType.Type == typeof(View_ExtURL);

                var viewInstance = new ViewInstance() {
                    viewID = v.ID,
                    viewName = v.Name,
                    viewURL = url ? v.Config.Object<ViewURLConfig>()!.URL : viewType.HtmlPath,
                    viewIcon = viewType.Icon,
                    viewGroup = v.Group,
                    viewType = v.Type,
                };

                result.views.Add(viewInstance);
            }
            return result;
        }

        private static void logWarn(string msg, Exception? exp = null) {
            Exception? exception = exp != null ? (exp.GetBaseException() ?? exp) : null;
            if (exception != null)
                Console.Out.WriteLine(msg + " " + exception.Message + "\n" + exception.StackTrace);
            else
                Console.Out.WriteLine(msg);
        }
    }

    public class ViewType
    {
        public string Name { get; set; } = "";
        public string HtmlPath { get; set; } = "";
        public string Icon { get; set; } = "";
        public Type? Type { get; set; }
        public Type? ConfigType { get; set; }
    }


    public class DashboardUI
    {
        public List<ViewInstance> views = new List<ViewInstance>();
    }

    public class ViewInstance
    {
        public string viewID { get; set; } = "";
        public string viewIcon { get; set; } = "";
        public string viewName { get; set; } = "";
        public string viewURL { get; set; } = "";
        public string viewGroup { get; set; } = "";
        public string viewType { get; set; } = "";
    }

    public class InvalidSessionException : Exception
    {
        public InvalidSessionException() : base("Invalid Session ID") { }
    }

    internal static class TaskUtil
    {
        internal static void CompleteFromTask(this TaskCompletionSource<bool> promise, Task completedTask) {

            if (completedTask.IsCompletedSuccessfully) {
                promise.SetResult(true);
            }
            else if (completedTask.IsFaulted) {
                promise.SetException(completedTask.Exception!);
            }
            else {
                promise.SetCanceled();
            }
        }
    }
}