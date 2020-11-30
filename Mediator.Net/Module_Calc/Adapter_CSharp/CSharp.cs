﻿// Licensed to ifak e.V. under one or more agreements.
// ifak e.V. licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Ifak.Fast.Mediator.Calc.Adapter_CSharp
{
    [Identify(id: "CSharp", showWindowVisible: false, showDefinition: true, definitionLabel: "Script", definitionIsCode: true)]
    public class CSharp : CalculationBase, EventSink
    {
        private InputBase[] inputs = new Input[0];
        private OutputBase[] outputs = new Output[0];
        private AbstractState[] states = new AbstractState[0];
        private Alarm[] alarms = new Alarm[0];
        private Action<Timestamp, Duration> stepAction = (t, dt) => { };
        private Duration dt = Duration.FromSeconds(1);
        private AdapterCallback callback;

        public override async Task<InitResult> Initialize(InitParameter parameter, AdapterCallback callback) {

            this.callback = callback;
            string code = parameter.Calculation.Definition;
            dt = parameter.Calculation.Cycle;

            if (!string.IsNullOrWhiteSpace(code)) {

                // var sw = System.Diagnostics.Stopwatch.StartNew();

                var config = new Mediator.Config(parameter.ModuleConfig);
                string libs = config.GetOptionalString("csharp-libraries", "");

                string[] assemblies = libs
                    .Split(new char[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToArray();

                string[] absoluteAssemblies = assemblies.Select(d => Path.GetFullPath(d)).ToArray();
                foreach (string assembly in absoluteAssemblies) {
                    if (!File.Exists(assembly)) throw new Exception($"csharp-library does not exist: {assembly}");
                }

                absoluteAssemblies = absoluteAssemblies.Select(assembly => {
                    if (assembly.ToLowerInvariant().EndsWith(".cs")) {
                        return CompileLib.CSharpFile2Assembly(assembly);
                    }
                    return assembly;
                }).ToArray();

                var referencedAssemblies = new List<Assembly>();
                referencedAssemblies.Add(typeof(System.Collections.Generic.IList<int>).Assembly);
                referencedAssemblies.Add(typeof(System.Linq.Enumerable).Assembly);
                referencedAssemblies.Add(typeof(Ifak.Fast.Mediator.Timestamp).Assembly);
                referencedAssemblies.Add(typeof(Input).Assembly);

                foreach (string assembly in absoluteAssemblies) {
                    try {
                        Assembly ass = Assembly.LoadFrom(assembly);
                        referencedAssemblies.Add(ass);
                    }
                    catch (Exception exp) {
                        throw new Exception($"Failed to load csharp-library {assembly}: {exp.Message}");
                    }
                }

                var options = ScriptOptions.Default
                    .WithImports(
                        "System",
                        "System.Collections.Generic",
                        "System.Linq",
                        "Ifak.Fast.Mediator.Calc.Adapter_CSharp",
                        "Ifak.Fast.Mediator")
                    .WithReferences(referencedAssemblies.ToArray());

                const string className = "Script";

                var script = CSharpScript.
                    Create<object>(code, options).
                    ContinueWith($"new {className}()");

                // var diag = script.Compile();

                ScriptState<object> scriptState = await script.RunAsync();
                object obj = scriptState.ReturnValue;

                // Console.WriteLine($"Time script: {sw.ElapsedMilliseconds} ms");

                inputs  = GetIdentifiableMembers<InputBase> (obj, "", recursive: false).ToArray();
                outputs = GetIdentifiableMembers<OutputBase>(obj, "", recursive: false).ToArray();
                states  = GetIdentifiableMembers<AbstractState>(obj, "", recursive: true).ToArray();

                var eventProviders = GetMembers<EventProvider>(obj, recursive: true);
                foreach (EventProvider provider in eventProviders) {
                    provider.EventSinkRef = this;
                }

                Type type = obj.GetType();

                MethodInfo[] methods =
                    type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Step" && IsStepSignature(m))
                    .ToArray();

                if (methods.Length == 0) throw new Exception("No Step(Timestamp t, TimeSpan dt) method found.");
                MethodInfo step = methods[0];

                stepAction = (Action<Timestamp, Duration>)step.CreateDelegate(typeof(Action<Timestamp, Duration>), obj);

                foreach (StateValue v in parameter.LastState) {
                    AbstractState state = states.FirstOrDefault(s => s.ID == v.StateID);
                    if (state != null) {
                        state.SetValueFromDataValue(v.Value);
                    }
                }

                return new InitResult() {
                    Inputs = inputs.Select(MakeInputDef).ToArray(),
                    Outputs = outputs.Select(MakeOutputDef).ToArray(),
                    States = states.Select(MakeStateDef).ToArray(),
                    ExternalStatePersistence = true
                };

            }
            else {

                return new InitResult() {
                    Inputs = new InputDef[0],
                    Outputs = new OutputDef[0],
                    States = new StateDef[0],
                    ExternalStatePersistence = true
                };
            }
        }

        public void Notify_AlarmOrEvent(AdapterAlarmOrEvent eventInfo) {
            callback.Notify_AlarmOrEvent(eventInfo);
        }

        private static bool IsStepSignature(MethodInfo m) {
            ParameterInfo[] parameters = m.GetParameters();
            if (parameters.Length != 2) return false;
            ParameterInfo p1 = parameters[0];
            ParameterInfo p2 = parameters[1];
            if (p1.ParameterType != typeof(Timestamp)) return false;
            if (p2.ParameterType != typeof(Duration)) return false;
            if (m.ReturnType != typeof(void)) return false;
            return true;
        }

        public override Task Shutdown() {
            return Task.FromResult(true);
        }

        public override Task<StepResult> Step(Timestamp t, InputValue[] inputValues) {

            foreach (InputValue v in inputValues) {
                InputBase input = inputs.FirstOrDefault(inn => inn.ID == v.InputID);
                if (input != null) {
                    input.VTQ = v.Value;
                }
            }

            stepAction(t, dt);

            StateValue[] resStates = states.Select(kv => new StateValue() {
                StateID = kv.ID,
                Value = kv.GetValue()
            }).ToArray();

            OutputValue[] result = outputs.Select(kv => new OutputValue() {
                OutputID = kv.ID,
                Value = kv.VTQ
            }).ToArray();

            var stepRes = new StepResult() {
                Output = result,
                State = resStates,
            };

            return Task.FromResult(stepRes);
        }

        private static List<T> GetIdentifiableMembers<T>(object obj, string idChain, bool recursive) where T : class, Identifiable {
            List<T> result = new List<T>();
            FieldInfo[] fields = obj.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            foreach (FieldInfo f in fields) {
                string id = f.Name;
                object fieldValue = f.GetValue(obj);
                if (fieldValue is T x) {
                    x.ID = idChain + id;
                    x.Name = idChain + x.Name;
                    result.Add(x);
                }
                else if (recursive && f.FieldType.IsClass) {
                    result.AddRange(GetIdentifiableMembers<T>(fieldValue, idChain + id + ".", recursive));
                }
            }
            return result;
        }

        private static List<T> GetMembers<T>(object obj, bool recursive) where T : class {
            List<T> result = new List<T>();
            FieldInfo[] fields = obj.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            foreach (FieldInfo f in fields) {
                object fieldValue = f.GetValue(obj);
                if (fieldValue is T x) {
                    result.Add(x);
                }
                else if (recursive && f.FieldType.IsClass) {
                    result.AddRange(GetMembers<T>(fieldValue, recursive));
                }
            }
            return result;
        }

        private static InputDef MakeInputDef(InputBase m) {
            return new InputDef() {
                ID = m.ID,
                Name = m.Name,
                Description = m.Name,
                Unit = m.Unit,
                Dimension = m.Dimension,
                Type = m.Type,
                DefaultValue = m.GetDefaultValue()
            };
        }

        private static StateDef MakeStateDef(AbstractState m) {
            return new StateDef() {
                ID = m.ID,
                Name = m.Name,
                Description = m.Name,
                Unit = m.Unit,
                Dimension = m.GetDimension(),
                Type = m.GetDataType(),
                DefaultValue = m.GetDefaultValue()
            };
        }

        private static OutputDef MakeOutputDef(OutputBase m) {
            return new OutputDef() {
                ID = m.ID,
                Name = m.Name,
                Description = m.Name,
                Unit = m.Unit,
                Dimension = m.Dimension,
                Type = m.Type,
            };
        }
    }
}
