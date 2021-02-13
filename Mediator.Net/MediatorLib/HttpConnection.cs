﻿// Licensed to ifak e.V. under one or more agreements.
// ifak e.V. licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Ifak.Fast.Json.Linq;
using Ifak.Fast.Mediator.Util;

namespace Ifak.Fast.Mediator
{
    public class HttpConnection : Connection
    {
        private string login = "";
        private Timestamp tLogin = Timestamp.Now;

        public static async Task<Connection> ConnectWithUserLogin(string host, int port, string login, string password, string[] roles = null, EventListener listener = null, int timeoutSeconds = 20) {

            if (host == null) throw new ArgumentNullException(nameof(host));
            if (login == null) throw new ArgumentNullException(nameof(login));
            if (password == null) throw new ArgumentNullException(nameof(password));

            var res = new HttpConnection(host, port, TimeSpan.FromSeconds(timeoutSeconds), $"User({login})");
            await res.DoConnectAndLogin(login, password, false, roles ?? new string[0], listener);
            return res;
        }

        public static async Task<Connection> ConnectWithModuleLogin(ModuleInitInfo info, EventListener listener = null, int timeoutSeconds = 60) {
            var res = new HttpConnection(info.LoginServer, info.LoginPort, TimeSpan.FromSeconds(timeoutSeconds), $"Module({info.ModuleID})");
            await res.DoConnectAndLogin(info.ModuleID, info.LoginPassword, true, new string[0], listener);
            return res;
        }

        protected readonly HttpClient client;
        protected readonly Uri wsUri;

        protected EventManager eventManager = null;
        protected string session = null;

        protected HttpConnection(string host, int port, TimeSpan timeout, string login) {

            this.login = login;

            Uri baseUri = new Uri("http://" + host + ":" + port + "/Mediator/");
            wsUri       = new Uri("ws://"   + host + ":" + port + "/Mediator/");
            client = new HttpClient();
            client.Timeout = timeout;
            client.BaseAddress = baseUri;
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        protected async Task DoConnectAndLogin(string login, string password, bool isModule, string[] roles, EventListener listener) {

            var request = new JObject();

            if (isModule) {
                request["moduleID"] = login;
            }
            else {
                request["login"] = login;
                request["roles"] = new JRaw(StdJson.ValueToString(roles));
            }

            JObject json = await PostJObject("Login", request);

            string session = (string)json["session"];
            string challenge = (string)json["challenge"];
            if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(challenge))
                throw new ConnectivityException("Invalid response");

            long hash = Util.ClientDefs.strHash(password + challenge + password + session);

            request = new JObject();
            request["session"] = session;
            request["hash"] = hash;

            json = await PostJObject("Authenticate", request);

            this.session = (string)json["session"];
            tLogin = Timestamp.Now;

            if (listener != null) {
                eventManager = new EventManager(listener);
                await eventManager.StartWebSocket(this.session, wsUri, OnConnectionBroken);
            }
        }

        public override bool IsClosed => session == null;

        public override async Task Close() {

            string session = this.session;
            if (session == null) return;
            this.session = null;

            eventManager?.Close();
            eventManager = null;

            var request = new JObject();
            request["session"] = session;
            try {
                await PostJObject("Logout", request);
            }
            catch (Exception) {
                // Console.Error.WriteLine("Exception in " + nameof(HttpConnection) + "." + nameof(Close) + ": " + exp.Message);
            }

            client.Dispose();
        }

        public override void Dispose() {
            Task ignored = Close();
        }

        protected void OnConnectionBroken(string context, Exception exp) {

            this.session = null;

            ReportConnectionBroken(login, tLogin, context, exp);

            eventManager?.Close();
            eventManager = null;
            client.Dispose();
        }

        private static void ReportConnectionBroken(string login, Timestamp tLogin, string context, Exception exp) {

            string now = Timestamp.Now.ToDateTime().ToLocalTime().ToString("HH':'mm':'ss'.'fff", System.Globalization.CultureInfo.InvariantCulture);
            string at = tLogin.ToDateTime().ToLocalTime().ToString("yyyy'-'MM'-'dd\u00A0HH':'mm':'ss", System.Globalization.CultureInfo.InvariantCulture);
            string s = $"{now}: ConnectionBroken in {context}; Login: {login} at {at}";
            if (exp != null) {
                s += Environment.NewLine;
                string indent = new string(' ', now.Length + 1);
                Exception e = exp.GetBaseException() ?? exp;
                s += $"{indent} --> {e.GetType().FullName}: {e.Message}";
            }
            Console.Error.WriteLine(s);
            Console.Error.Flush();
        }

        #region Methods

        public override async Task EnableAlarmsAndEvents(Severity minSeverity = Severity.Info) {
            JObject request = MakeSessionRequest();
            request["minSeverity"] = new JRaw(StdJson.ValueToString(minSeverity));
            await PostJObject("EnableAlarmsAndEvents", request);
        }

        public override async Task DisableAlarmsAndEvents() {
            JObject request = MakeSessionRequest();
            await PostJObject("DisableAlarmsAndEvents", request);
        }

        public override async Task EnableConfigChangedEvents(params ObjectRef[] objects) {
            JObject request = MakeSessionRequest();
            request["objects"] = new JRaw(StdJson.ValueToString(objects));
            await PostJObject("EnableConfigChangedEvents", request);
        }

        public override async Task EnableVariableHistoryChangedEvents(params VariableRef[] variables) {
            JObject request = MakeSessionRequest();
            request["variables"] = new JRaw(StdJson.ObjectToString(variables));
            await PostJObject("EnableVariableHistoryChangedEvents", request);
        }

        public override async Task EnableVariableHistoryChangedEvents(params ObjectRef[] idsOfEnabledTreeRoots) {
            JObject request = MakeSessionRequest();
            request["idsOfEnabledTreeRoots"] = new JRaw(StdJson.ValueToString(idsOfEnabledTreeRoots));
            await PostJObject("EnableVariableHistoryChangedEvents", request);
        }

        public override async Task EnableVariableValueChangedEvents(SubOptions options, params VariableRef[] variables) {
            JObject request = MakeSessionRequest();
            request["options"] = new JRaw(StdJson.ObjectToString(options));
            request["variables"] = new JRaw(StdJson.ObjectToString(variables));
            await PostJObject("EnableVariableValueChangedEvents", request);
        }

        public override async Task EnableVariableValueChangedEvents(SubOptions options, params ObjectRef[] idsOfEnabledTreeRoots) {
            JObject request = MakeSessionRequest();
            request["options"] = new JRaw(StdJson.ObjectToString(options));
            request["idsOfEnabledTreeRoots"] = new JRaw(StdJson.ValueToString(idsOfEnabledTreeRoots));
            await PostJObject("EnableVariableValueChangedEvents", request);
        }

        public override async Task DisableChangeEvents(bool disableVarValueChanges, bool disableVarHistoryChanges, bool disableConfigChanges) {
            JObject request = MakeSessionRequest();
            request["disableVarValueChanges"] = disableVarValueChanges;
            request["disableVarHistoryChanges"] = disableVarHistoryChanges;
            request["disableConfigChanges"] = disableConfigChanges;
            await PostJObject("DisableChangeEvents", request);
        }

        public override async Task<User> GetLoginUser() {
            JObject request = MakeSessionRequest();
            return await Post<User>("GetLoginUser", request);
        }

        public override async Task<ModuleInfo[]> GetModules() {
            JObject request = MakeSessionRequest();
            return await Post<ModuleInfo[]>("GetModules", request);
        }

        public override async Task<LocationInfo[]> GetLocations() {
            JObject request = MakeSessionRequest();
            return await Post<LocationInfo[]>("GetLocations", request);
        }

        public override async Task<ObjectInfo[]> GetAllObjects(string moduleID) {
            JObject request = MakeSessionRequest();
            request["moduleID"] = new JRaw(StdJson.ValueToString(moduleID));
            return await Post<ObjectInfo[]>("GetAllObjects", request);
        }

        public override async Task<ObjectInfo[]> GetAllObjectsOfType(string moduleID, string className) {
            JObject request = MakeSessionRequest();
            request["moduleID"] = new JRaw(StdJson.ValueToString(moduleID));
            request["className"] = new JRaw(StdJson.ValueToString(className));
            return await Post<ObjectInfo[]>("GetAllObjectsOfType", request);
        }

        public override async Task<ObjectInfo[]> GetAllObjectsWithVariablesOfType(string moduleID, params DataType[] types) {
            JObject request = MakeSessionRequest();
            request["moduleID"] = new JRaw(StdJson.ValueToString(moduleID));
            request["types"] = new JRaw(StdJson.ValueToString(types.Cast<Enum>().ToArray()));
            return await Post<ObjectInfo[]>("GetAllObjectsWithVariablesOfType", request);
        }

        public override async Task<ObjectInfo[]> GetChildrenOfObjects(params ObjectRef[] objectIDs) {
            JObject request = MakeSessionRequest();
            request["objectIDs"] = new JRaw(StdJson.ValueToString(objectIDs));
            return await Post<ObjectInfo[]>("GetChildrenOfObjects", request);
        }

        public override async Task<MemberValue[]> GetMemberValues(MemberRef[] member) {
            if (member == null) throw new ArgumentNullException(nameof(member));
            JObject request = MakeSessionRequest();
            request["member"] = new JRaw(StdJson.ObjectToString(member));
            return await Post<MemberValue[]>("GetMemberValues", request);
        }

        public override async Task<MetaInfos> GetMetaInfos(string moduleID) {
            if (moduleID == null) throw new ArgumentNullException(nameof(moduleID));
            JObject request = MakeSessionRequest();
            request["moduleID"] = new JRaw(StdJson.ValueToString(moduleID));
            return await Post<MetaInfos>("GetMetaInfos", request);
        }

        public override async Task<ObjectInfo[]> GetObjectsByID(params ObjectRef[] objectIDs) {
            JObject request = MakeSessionRequest();
            request["objectIDs"] = new JRaw(StdJson.ValueToString(objectIDs));
            return await Post<ObjectInfo[]>("GetObjectsByID", request);
        }

        public override async Task<ObjectValue[]> GetObjectValuesByID(params ObjectRef[] objectIDs) {
            JObject request = MakeSessionRequest();
            request["objectIDs"] = new JRaw(StdJson.ValueToString(objectIDs));
            return await Post<ObjectValue[]>("GetObjectValuesByID", request);
        }

        public override async Task<ObjectValue> GetParentOfObject(ObjectRef objectID) {
            JObject request = MakeSessionRequest();
            request["objectID"] = new JRaw(StdJson.ValueToString(objectID));
            return await Post<ObjectValue>("GetParentOfObject", request);
        }

        public override async Task<ObjectInfo> GetRootObject(string moduleID) {
            JObject request = MakeSessionRequest();
            request["moduleID"] = new JRaw(StdJson.ValueToString(moduleID));
            return await Post<ObjectInfo>("GetRootObject", request);
        }

        public override async Task<long> HistorianCount(VariableRef variable, Timestamp startInclusive, Timestamp endInclusive, QualityFilter filter = QualityFilter.ExcludeNone) {
            JObject request = MakeSessionRequest();
            request["variable"] = new JRaw(StdJson.ObjectToString(variable));
            request["startInclusive"] = new JRaw(StdJson.ValueToString(startInclusive));
            request["endInclusive"] = new JRaw(StdJson.ValueToString(endInclusive));
            request["filter"] = new JRaw(StdJson.ValueToString(filter));
            return await Post<long>("HistorianCount", request);
        }

        public override async Task HistorianDeleteAllVariablesOfObjectTree(ObjectRef objectID) {
            JObject request = MakeSessionRequest();
            request["objectID"] = new JRaw(StdJson.ValueToString(objectID));
            await PostJObject("HistorianDeleteAllVariablesOfObjectTree", request);
        }

        public override async Task HistorianDeleteVariables(params VariableRef[] variables) {
            JObject request = MakeSessionRequest();
            request["variables"] = new JRaw(StdJson.ObjectToString(variables));
            await PostJObject("HistorianDeleteVariables", request);
        }

        public override async Task<long> HistorianDeleteInterval(VariableRef variable, Timestamp startInclusive, Timestamp endInclusive) {
            JObject request = MakeSessionRequest();
            request["variable"] = new JRaw(StdJson.ObjectToString(variable));
            request["startInclusive"] = new JRaw(StdJson.ValueToString(startInclusive));
            request["endInclusive"] = new JRaw(StdJson.ValueToString(endInclusive));
            return await Post<long>("HistorianDeleteInterval", request);
        }

        public override async Task<VTTQ?> HistorianGetLatestTimestampDB(VariableRef variable, Timestamp startInclusive, Timestamp endInclusive) {
            JObject request = MakeSessionRequest();
            request["variable"] = new JRaw(StdJson.ObjectToString(variable));
            request["startInclusive"] = new JRaw(StdJson.ValueToString(startInclusive));
            request["endInclusive"] = new JRaw(StdJson.ValueToString(endInclusive));
            return await Post<VTTQ?>("HistorianGetLatestTimestampDB", request);
        }

        public override async Task HistorianModify(VariableRef variable, ModifyMode mode, params VTQ[] data) {
            JObject request = MakeSessionRequest();
            request["variable"] = new JRaw(StdJson.ObjectToString(variable));
            request["data"] = new JRaw(StdJson.ObjectToString(data));
            request["mode"] = new JRaw(StdJson.ValueToString(mode));
            await PostJObject("HistorianModify", request);
        }

        public override async Task<VTTQ[]> HistorianReadRaw(VariableRef variable, Timestamp startInclusive, Timestamp endInclusive, int maxValues, BoundingMethod bounding, QualityFilter filter = QualityFilter.ExcludeNone) {
            JObject request = MakeSessionRequest();
            request["variable"] = new JRaw(StdJson.ObjectToString(variable));
            request["startInclusive"] = new JRaw(StdJson.ValueToString(startInclusive));
            request["endInclusive"] = new JRaw(StdJson.ValueToString(endInclusive));
            request["maxValues"] = new JRaw(StdJson.ValueToString(maxValues));
            request["bounding"] = new JRaw(StdJson.ValueToString(bounding));
            request["filter"] = new JRaw(StdJson.ValueToString(filter));
            return await Post<VTTQ[]>("HistorianReadRaw", request);
        }

        public override async Task<VariableValue[]> ReadAllVariablesOfObjectTree(ObjectRef objectID) {
            JObject request = MakeSessionRequest();
            request["objectID"] = new JRaw(StdJson.ValueToString(objectID));
            return await Post<VariableValue[]>("ReadAllVariablesOfObjectTree", request);
        }

        public override async Task<VTQ[]> ReadVariables(VariableRef[] variables) {
            if (variables == null) throw new ArgumentNullException(nameof(variables));
            JObject request = MakeSessionRequest();
            request["variables"] = new JRaw(StdJson.ObjectToString(variables));
            return await Post<VTQ[]>("ReadVariables", request);
        }

        public override async Task<VariableValue[]> ReadVariablesIgnoreMissing(VariableRef[] variables) {
            if (variables == null) throw new ArgumentNullException(nameof(variables));
            JObject request = MakeSessionRequest();
            request["variables"] = new JRaw(StdJson.ObjectToString(variables));
            return await Post<VariableValue[]>("ReadVariablesIgnoreMissing", request);
        }

        public override async Task<VTQ[]> ReadVariablesSync(VariableRef[] variables, Duration? timeout = null) {
            if (variables == null) throw new ArgumentNullException(nameof(variables));
            JObject request = MakeSessionRequest();
            request["variables"] = new JRaw(StdJson.ObjectToString(variables));
            if (timeout.HasValue) {
                request["timeout"] = new JRaw(StdJson.ValueToString(timeout.Value));
            }
            Task<VTQ[]> task = Post<VTQ[]>("ReadVariablesSync", request);
            if (timeout.HasValue) {
                if (task == await Task.WhenAny(task, Task.Delay(timeout.Value.ToTimeSpan()))) {
                    return await task;
                }
                else {
                    throw new Exception("Timeout");
                }
            }
            else {
                return await task;
            }
        }

        public override async Task<VariableValue[]> ReadVariablesSyncIgnoreMissing(VariableRef[] variables, Duration? timeout = null) {
            if (variables == null) throw new ArgumentNullException(nameof(variables));
            JObject request = MakeSessionRequest();
            request["variables"] = new JRaw(StdJson.ObjectToString(variables));
            if (timeout.HasValue) {
                request["timeout"] = new JRaw(StdJson.ValueToString(timeout.Value));
            }
            Task<VariableValue[]> task = Post<VariableValue[]>("ReadVariablesSyncIgnoreMissing", request);
            if (timeout.HasValue) {
                if (task == await Task.WhenAny(task, Task.Delay(timeout.Value.ToTimeSpan()))) {
                    return await task;
                }
                else {
                    throw new Exception("Timeout");
                }
            }
            else {
                return await task;
            }
        }

        public override async Task UpdateConfig(ObjectValue[] updateOrDeleteObjects, MemberValue[] updateOrDeleteMembers, AddArrayElement[] addArrayElements) {
            JObject request = MakeSessionRequest();
            if (updateOrDeleteObjects != null && updateOrDeleteObjects.Length > 0) {
                request["updateOrDeleteObjects"] = new JRaw(StdJson.ObjectToString(updateOrDeleteObjects));
            }
            if (updateOrDeleteMembers != null && updateOrDeleteMembers.Length > 0) {
                request["updateOrDeleteMembers"] = new JRaw(StdJson.ObjectToString(updateOrDeleteMembers));
            }
            if (addArrayElements != null && addArrayElements.Length > 0) {
                request["addArrayElements"] = new JRaw(StdJson.ObjectToString(addArrayElements));
            }
            await PostJObject("UpdateConfig", request);
        }

        public override Task WriteVariables(VariableValue[] values) {
            JObject request = MakeSessionRequest();
            request["values"] = new JRaw(StdJson.ObjectToString(values));
            return PostJObject("WriteVariables", request);
        }

        public override Task<WriteResult> WriteVariablesIgnoreMissing(VariableValue[] values) {
            JObject request = MakeSessionRequest();
            request["values"] = new JRaw(StdJson.ObjectToString(values));
            return Post<WriteResult>("WriteVariablesIgnoreMissing", request);
        }

        public override async Task<WriteResult> WriteVariablesSync(VariableValue[] values, Duration? timeout = null) {
            JObject request = MakeSessionRequest();
            request["values"] = new JRaw(StdJson.ObjectToString(values));
            if (timeout.HasValue) {
                request["timeout"] = new JRaw(StdJson.ValueToString(timeout.Value));
            }
            Task<WriteResult> task = Post<WriteResult>("WriteVariablesSync", request);
            if (timeout.HasValue) {
                if (task == await Task.WhenAny(task, Task.Delay(timeout.Value.ToTimeSpan()))) {
                    return await task;
                }
                else {
                    throw new Exception("Timeout");
                }
            }
            else {
                return await task;
            }
        }

        public override async Task<WriteResult> WriteVariablesSyncIgnoreMissing(VariableValue[] values, Duration? timeout = null) {
            JObject request = MakeSessionRequest();
            request["values"] = new JRaw(StdJson.ObjectToString(values));
            if (timeout.HasValue) {
                request["timeout"] = new JRaw(StdJson.ValueToString(timeout.Value));
            }
            Task<WriteResult> task = Post<WriteResult>("WriteVariablesSyncIgnoreMissing", request);
            if (timeout.HasValue) {
                if (task == await Task.WhenAny(task, Task.Delay(timeout.Value.ToTimeSpan()))) {
                    return await task;
                }
                else {
                    throw new Exception("Timeout");
                }
            }
            else {
                return await task;
            }
        }

        public override Task<DataValue> CallMethod(string moduleID, string methodName, params NamedValue[] parameters) {
            JObject request = MakeSessionRequest();
            request["moduleID"] = moduleID;
            request["methodName"] = methodName;
            request["parameters"] = new JRaw(StdJson.ObjectToString(parameters));
            return Post<DataValue>("CallMethod", request);
        }

        public override Task<BrowseResult> BrowseObjectMemberValues(MemberRef member, int? continueID = null) {
            JObject request = MakeSessionRequest();
            request["member"] = new JRaw(StdJson.ObjectToString(member));
            if (continueID.HasValue) {
                request["continueID"] = new JRaw(StdJson.ValueToString(continueID.Value));
            }
            return Post<BrowseResult>("BrowseObjectMemberValues", request);
        }

        #endregion

        protected async Task<JObject> PostJObject(string path, JObject obj) {

            var payload = new StringContent(StdJson.ObjectToString(obj), Encoding.UTF8);

            HttpResponseMessage response = null;
            try {
                response = await client.PostAsync(path, payload);
            }
            catch (TaskCanceledException exp) {
                OnConnectionBroken($"PostJObject {path} client.PostAsync TaskCanceled", exp);
                throw new ConnectivityException("Time out");
            }
            catch (Exception exp) {
                OnConnectionBroken($"PostJObject {path} client.PostAsync", exp);
                throw new ConnectivityException(exp.Message);
            }

            using (response) {
                if (response.IsSuccessStatusCode) {
                    try {
                        string content = await response.Content.ReadAsStringAsync();
                        if (string.IsNullOrEmpty(content)) return new JObject();
                        return StdJson.JObjectFromString(content);
                    }
                    catch (TaskCanceledException exp) {
                        OnConnectionBroken($"PostJObject {path} response.Content.ReadAsStringAsync TaskCanceled", exp);
                        throw new ConnectivityException("Time out");
                    }
                    catch (Exception exp) {
                        OnConnectionBroken($"PostJObject {path} response.Content.ReadAsStringAsync", exp);
                        throw new ConnectivityException(exp.Message);
                    }
                }
                else {
                    await ThrowError(response, $"PostJObject {path}");
                    return null; // never come here
                }
            }
        }

        protected async Task<T> Post<T>(string path, JObject obj) {

            var requestStream = MemoryManager.GetMemoryStream("HttpConnection.Post");
            try {
                StdJson.ObjectToStream(obj, requestStream);
                requestStream.Seek(0, SeekOrigin.Begin);
            }
            catch (Exception) {
                requestStream.Dispose();
                throw;
            }

            HttpResponseMessage response = null;

            using (var payload = new StreamContent(requestStream)) {

                try {
                    response = await client.PostAsync(path, payload);
                }
                catch (TaskCanceledException exp) {
                    OnConnectionBroken($"Post<T> {path} client.PostAsync TaskCanceled", exp);
                    throw new ConnectivityException("Time out");
                }
                catch (Exception exp) {
                    OnConnectionBroken($"Post<T> {path} client.PostAsync", exp);
                    throw new ConnectivityException(exp.Message);
                }
            }

            using (response) {
                if (response.IsSuccessStatusCode) {
                    try {
                        Stream stream = await response.Content.ReadAsStreamAsync();
                        using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                            return StdJson.ObjectFromReader<T>(reader);
                        }
                    }
                    catch (TaskCanceledException exp) {
                        OnConnectionBroken($"Post<T> {path} response.Content.ReadAsStreamAsync TaskCanceled", exp);
                        throw new ConnectivityException("Time out");
                    }
                    catch (Exception exp) {
                        OnConnectionBroken($"Post<T> {path} response.Content.ReadAsStreamAsync", exp);
                        throw new ConnectivityException(exp.Message);
                    }
                }
                else {
                    await ThrowError(response, $"Post<T> {path}");
                    return default(T); // never come here
                }
            }
        }

        protected async Task ThrowError(HttpResponseMessage response, string context) {

            string content = null;
            try {
                content = await response.Content.ReadAsStringAsync();
            }
            catch (Exception) {}

            JObject errObj = null;
            try {
                errObj = StdJson.JObjectFromString(content);
            }
            catch (Exception) {}

            const string PropertyError = "error";
            if (errObj == null || errObj.Property(PropertyError) == null) {
                string errMsg = string.IsNullOrWhiteSpace(content) ? response.StatusCode.ToString() : content;
                OnConnectionBroken($"ThrowError {context} '{errMsg}'", null);
                throw new ConnectivityException(errMsg);
            }
            else {
                string errMsg = GetStringPropertyOrDefault(errObj, PropertyError, response.StatusCode.ToString());
                throw new RequestException(errMsg);
            }
        }

        private bool GetBoolPropertyOrDefault(JObject obj, string name, bool defaultValue) {
            try {
                return (bool)obj[name];
            }
            catch {
                return defaultValue;
            }
        }

        private string GetStringPropertyOrDefault(JObject obj, string name, string defaultValue) {
            try {
                return (string)obj[name];
            }
            catch {
                return defaultValue;
            }
        }

        private const string ConnectionClosedMessage = "Connection is closed.";

        private JObject MakeSessionRequest() {
            if (IsClosed) throw new ConnectivityException(ConnectionClosedMessage);
            JObject request = new JObject();
            request["session"] = session;
            return request;
        }

        public class EventManager
        {
            protected readonly EventListener listener;

            protected CancellationTokenSource webSocketCancel;
            protected ClientWebSocket webSocket;

            public EventManager(EventListener listener) {
                this.listener = listener;
            }

            public async Task StartWebSocket(string session, Uri wsUri, Action<string, Exception> notifyConnectionBroken) {

                webSocketCancel = new CancellationTokenSource();
                webSocket = new ClientWebSocket();

                await webSocket.ConnectAsync(wsUri, CancellationToken.None);

                var sendBuffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(session));
                await webSocket.SendAsync(sendBuffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: CancellationToken.None);

                var t = ReadWebSocketForEvents(webSocketCancel.Token, notifyConnectionBroken);
            }

            protected async Task ReadWebSocketForEvents(CancellationToken cancelToken, Action<string, Exception> notifyConnectionBroken) {

                byte[] bytesOK = new byte[] { (byte)'O', (byte)'K' };
                ArraySegment<byte> ok = new ArraySegment<byte>(bytesOK);
                var buffer = new ArraySegment<byte>(new byte[8192]);
                var stream = new MemoryStream(8192);

                while (!cancelToken.IsCancellationRequested) {

                    stream.Seek(0, SeekOrigin.Begin);

                    WebSocketReceiveResult result = null;
                    do {
                        try {
                            result = await webSocket.ReceiveAsync(buffer, cancelToken);
                        }
                        catch (Exception exp) {
                            var t = CloseSocket(); // no need to wait for completion
                            if (!cancelToken.IsCancellationRequested) {
                                notifyConnectionBroken("ReadWebSocketForEvents ReceiveAsync", exp);
                            }
                            Task ignored = listener.OnConnectionClosed();
                            return;
                        }
                        stream.Write(buffer.Array, buffer.Offset, result.Count);
                    }
                    while (!result.EndOfMessage);

                    stream.Seek(0, SeekOrigin.Begin);

                    if (result.MessageType == WebSocketMessageType.Text) {
                        JObject eventObj = null;
                        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true)) {
                            eventObj = StdJson.JObjectFromReader(reader);
                        }

                        try {
                            await DispatchEvent(eventObj);
                        }
                        catch (Exception exp) {
                            Exception exception = exp.GetBaseException() ?? exp;
                            string msg = "Exception in event dispatch: " + exception.Message;
                            if (exception is ConnectivityException) {
                                Console.Out.WriteLine(msg);
                            }
                            else {
                                Console.Error.WriteLine(msg);
                            }
                        }

                        try {
                            await webSocket.SendAsync(ok, WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        catch (Exception exp) {
                            var t = CloseSocket(); // no need to wait for completion
                            if (!cancelToken.IsCancellationRequested) {
                                notifyConnectionBroken("ReadWebSocketForEvents SendAsync(ok)", exp);
                            }
                            Task ignored = listener.OnConnectionClosed();
                            return;
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close) {
                        await CloseSocket();
                        if (!cancelToken.IsCancellationRequested) {
                            notifyConnectionBroken($"ReadWebSocketForEvents Close message received '{result.CloseStatusDescription}'", null);
                        }
                        await listener.OnConnectionClosed();
                        return;
                    }
                }
            }

            private async Task CloseSocket() {
                try {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch (Exception) { }
            }

            public void Close() {
                try {
                    webSocketCancel?.Cancel();
                }
                catch (Exception) { }
                try {
                    webSocketCancel?.Dispose();
                }
                catch (Exception) { }
                webSocketCancel = null;
            }

            protected Task DispatchEvent(JObject theEvent) {

                string eventName = (string)theEvent["event"];

                switch (eventName) {
                    case "OnVariableValueChanged": {
                            JArray jVariables = (JArray)theEvent["variables"];
                            VariableValue[] variables = StdJson.ObjectFromJToken<VariableValue[]>(jVariables);
                            return listener.OnVariableValueChanged(variables);
                        }

                    case "OnVariableHistoryChanged": {
                            JArray jChanges = (JArray)theEvent["changes"];
                            HistoryChange[] variables = StdJson.ObjectFromJToken<HistoryChange[]>(jChanges);
                            return listener.OnVariableHistoryChanged(variables);
                        }

                    case "OnConfigChanged": {
                            JArray jChanges = (JArray)theEvent["changedObjects"];
                            ObjectRef[] changes = StdJson.ObjectFromJToken<ObjectRef[]>(jChanges);
                            return listener.OnConfigChanged(changes);
                        }

                    case "OnAlarmOrEvent": {
                            AlarmOrEvent[] alarmOrEvents = StdJson.ObjectFromJToken<AlarmOrEvent[]>(theEvent["events"]);
                            return listener.OnAlarmOrEvents(alarmOrEvents);
                        }

                    default:
                        Console.Error.WriteLine("Unknown event: " + eventName);
                        return Task.FromResult(true);
                }
            }
        }
    }
}
