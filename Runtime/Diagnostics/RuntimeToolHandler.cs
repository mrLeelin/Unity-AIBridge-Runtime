using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Unity.Profiling;
using UObject = UnityEngine.Object;

namespace AIBridge.Runtime.Diagnostics
{
    /// <summary>通用真机对象检查、Profiler 会话和程序集身份诊断。</summary>
    internal sealed class RuntimeToolHandler : IAIBridgeHandler, IDisposable
    {
        private readonly Dictionary<string, WeakReference> _objects = new Dictionary<string, WeakReference>();
        private readonly Dictionary<string, Capture> _captures = new Dictionary<string, Capture>();
        private readonly AIBridgeRuntimeSettings _settings;
        internal RuntimeToolHandler(AIBridgeRuntimeSettings settings) { _settings = settings ?? throw new ArgumentNullException(nameof(settings)); }
        public string[] SupportedActions => new[] { "runtime.hierarchy", "runtime.inspect", "runtime.member.get", "runtime.member.set",
            "runtime.method.invoke", "runtime.profiler.start", "runtime.profiler.read", "runtime.profiler.stop", "runtime.assemblies" };

        public AIBridgeRuntimeCommandResult HandleCommand(AIBridgeRuntimeCommand c)
        {
            try {
                object result;
                switch (c.Action) {
                    case "runtime.hierarchy": result = Hierarchy(c); break;
                    case "runtime.inspect": result = Inspect(c); break;
                    case "runtime.member.get": result = Get(c); break;
                    case "runtime.member.set": result = Set(c); break;
                    case "runtime.method.invoke": result = Invoke(c); break;
                    case "runtime.profiler.start": result = Start(c); break;
                    case "runtime.profiler.read": result = Read(c, false); break;
                    case "runtime.profiler.stop": result = Read(c, true); break;
                    case "runtime.assemblies": result = Assemblies(); break;
                    default: return null;
                }
                return AIBridgeRuntimeCommandResult.FromSuccess(c.Id, result);
            } catch (Exception e) {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                return AIBridgeRuntimeCommandResult.FromFailure(c.Id, e.GetType().Name + ": " + e.Message);
            }
        }

        internal void Tick() { foreach (var c in _captures.Values) if (!c.Stopped && Time.realtimeSinceStartup >= c.Deadline) c.Stop(); }
        public void Dispose() { foreach (var c in _captures.Values) c.Stop(); _captures.Clear(); _objects.Clear(); }

        private string Remember(UObject value)
        {
            if (value == null) return null;
            foreach (var pair in _objects) if (ReferenceEquals(pair.Value.Target, value)) return pair.Key;
            foreach (var key in _objects.Where(p => !(p.Value.Target is UObject o) || o == null).Select(p => p.Key).ToArray()) _objects.Remove(key);
            if (_objects.Count >= 4096) throw new InvalidOperationException("Object handle limit reached; restart the Runtime service.");
            var token = Guid.NewGuid().ToString("N"); _objects[token] = new WeakReference(value); return token;
        }
        private UObject Resolve(AIBridgeRuntimeCommand c)
        {
            var token = c.GetParam<string>("objectToken");
            if (token == null || !_objects.TryGetValue(token, out var r) || !(r.Target is UObject o) || o == null)
                throw new InvalidOperationException("Object destroyed or handle expired; refresh hierarchy.");
            return o;
        }
        private object Reference(UObject o) => o == null ? null : new { objectToken = Remember(o), instanceId = o.GetInstanceID(), name = o.name, type = o.GetType().FullName };
        private object Hierarchy(AIBridgeRuntimeCommand c)
        {
            int offset = Math.Max(0, c.GetParam("offset", 0)), limit = Math.Max(1, Math.Min(500, c.GetParam("limit", 100)));
            var query = c.GetParam("query", "");
            var items = Resources.FindObjectsOfTypeAll<GameObject>().Where(o => o.scene.IsValid() && o.scene.isLoaded
                && o.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(o => o.GetInstanceID()).ToArray();
            return new { total = items.Length, offset, items = items.Skip(offset).Take(limit).Select(o => new {
                reference = Reference(o), parent = Reference(o.transform.parent == null ? null : o.transform.parent.gameObject),
                scene = o.scene.name, activeSelf = o.activeSelf, activeInHierarchy = o.activeInHierarchy }).ToArray() };
        }
        private static bool Sensitive(string n) => new[] { "token", "password", "secret" }.Any(s => n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
        private object Value(object v)
        {
            if (v == null) return null;
            if (v is UObject o) return Reference(o);
            if (v is Vector3 p) return new { x = p.x, y = p.y, z = p.z };
            if (v is Vector2 p2) return new { x = p2.x, y = p2.y };
            if (v is Quaternion q) return new { x = q.x, y = q.y, z = q.z, w = q.w };
            if (v is Color color) return new { r = color.r, g = color.g, b = color.b, a = color.a };
            if (v is string s) return s.Length <= 2048 ? s : s.Substring(0, 2048);
            if (v.GetType().IsPrimitive || v is decimal) return v;
            if (v.GetType().IsEnum) return v.ToString();
            return new { type = v.GetType().FullName, count = v is ICollection list ? (int?)list.Count : null };
        }
        private object Inspect(AIBridgeRuntimeCommand c)
        {
            var target = Resolve(c); var fields = new List<object>();
            for (var t = target.GetType(); t != null && fields.Count < 128; t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                    if (fields.Count >= 128) break;
                    fields.Add(new { name = f.Name, declaringType = t.FullName, type = f.FieldType.FullName,
                        writable = f.IsPublic && !f.IsInitOnly && !f.IsLiteral, value = Sensitive(f.Name) ? "<REDACTED>" : Value(f.GetValue(target)) });
                }
            var go = target as GameObject ?? (target as Component)?.gameObject;
            return new { reference = Reference(target), fields, propertiesEvaluated = false,
                properties = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0)
                    .Take(128).Select(p => new { name = p.Name, type = p.PropertyType.FullName, readable = p.GetGetMethod() != null, writable = p.GetSetMethod() != null }).ToArray(),
                components = go == null ? null : go.GetComponents<Component>().Select(Reference).ToArray(),
                transform = go == null ? null : new { reference = Reference(go.transform), position = Value(go.transform.position), localPosition = Value(go.transform.localPosition), localScale = Value(go.transform.localScale) },
                methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName).Take(128).Select(m => m.ToString()).ToArray() };
        }
        private void Mutate(AIBridgeRuntimeCommand c)
        {
            if (!_settings.enableRuntimeCodeExecution) throw new InvalidOperationException("Runtime code execution/mutation is disabled.");
            if (!c.GetParam("riskAccepted", false)) throw new InvalidOperationException("Mutation requires riskAccepted=true.");
        }
        private static object ConvertValue(object v, Type type)
        {
            if (v == null) { if (type.IsValueType) throw new ArgumentException("Null value type."); return null; }
            if (type == typeof(Vector3) && v is IDictionary<string, object> p) return new Vector3(Convert.ToSingle(p["x"]), Convert.ToSingle(p["y"]), Convert.ToSingle(p["z"]));
            if (type == typeof(Vector2) && v is IDictionary<string, object> p2) return new Vector2(Convert.ToSingle(p2["x"]), Convert.ToSingle(p2["y"]));
            if (type == typeof(Quaternion) && v is IDictionary<string, object> q) return new Quaternion(Convert.ToSingle(q["x"]), Convert.ToSingle(q["y"]), Convert.ToSingle(q["z"]), Convert.ToSingle(q["w"]));
            if (type == typeof(Color) && v is IDictionary<string, object> color) return new Color(Convert.ToSingle(color["r"]), Convert.ToSingle(color["g"]), Convert.ToSingle(color["b"]), Convert.ToSingle(color["a"]));
            if (type.IsEnum) return Enum.Parse(type, Convert.ToString(v), false);
            if (type == typeof(string) || type.IsPrimitive || type == typeof(decimal)) return Convert.ChangeType(v, type, System.Globalization.CultureInfo.InvariantCulture);
            throw new ArgumentException("Only scalar, enum, Vector2/3, Quaternion and Color arguments are supported.");
        }
        private object Get(AIBridgeRuntimeCommand c)
        {
            var target = Resolve(c); var name = c.GetParam<string>("member");
            if (string.IsNullOrWhiteSpace(name) || Sensitive(name)) throw new ArgumentException("Invalid member.");
            var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return new { value = Value(field.GetValue(target)) };
            Mutate(c); // 用户属性 getter 可能分配资源或修改状态，必须显式执行。
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.GetGetMethod() == null || property.GetIndexParameters().Length != 0) throw new ArgumentException("Readable public member required.");
            return new { value = Value(property.GetValue(target, null)) };
        }
        private object Set(AIBridgeRuntimeCommand c)
        {
            Mutate(c); var target = Resolve(c); var name = c.GetParam<string>("member");
            if (string.IsNullOrWhiteSpace(name) || Sensitive(name)) throw new ArgumentException("Invalid member.");
            if (!c.Params.TryGetValue("value", out var value)) throw new ArgumentException("value is required.");
            var f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && !f.IsInitOnly && !f.IsLiteral) f.SetValue(target, ConvertValue(value, f.FieldType));
            else {
                var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || p.GetSetMethod() == null || p.GetIndexParameters().Length != 0) throw new ArgumentException("Writable public member required.");
                p.SetValue(target, ConvertValue(value, p.PropertyType), null);
            }
            return new { updated = true, member = name, reference = Reference(target) };
        }
        private object Invoke(AIBridgeRuntimeCommand c)
        {
            Mutate(c); var target = Resolve(c); var signature = c.GetParam<string>("signature");
            var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => !m.ContainsGenericParameters && m.ToString() == signature).ToArray();
            if (methods.Length != 1) throw new ArgumentException("An exact unambiguous public signature is required.");
            var args = c.GetParam<object>("arguments") as IList ?? new object[0]; var parameters = methods[0].GetParameters();
            if (args.Count != parameters.Length || parameters.Any(p => p.ParameterType.IsByRef)) throw new ArgumentException("Argument signature mismatch.");
            return new { returnValue = Value(methods[0].Invoke(target, parameters.Select((p, i) => ConvertValue(args[i], p.ParameterType)).ToArray())) };
        }
        private object Assemblies() => new { items = AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name).Select(a => {
            string mvid = null, reason = null;
            try { mvid = a.ManifestModule.ModuleVersionId.ToString("D"); } catch (Exception e) { reason = e.Message; }
            return new { name = a.GetName().Name, version = a.GetName().Version.ToString(), mvid, identityAvailable = mvid != null && mvid != Guid.Empty.ToString("D"), reason,
                executionMode = "unknown; assembly identity does not distinguish per-method AOT/interpreter execution",
                eligibility = "Identity is not patch eligibility: native support, baseline and structure must also match." };
        }).ToArray() };
        private object Start(AIBridgeRuntimeCommand c)
        {
            if (_captures.Count >= 8) throw new InvalidOperationException("Capture limit is 8; stop with release=true.");
            var marker = c.GetParam<string>("marker");
            if (string.IsNullOrWhiteSpace(marker) || marker.Length > 256) throw new ArgumentException("Registered marker name required.");
            var category = c.GetParam("category", "Scripts");
            var categories = new Dictionary<string, ProfilerCategory> { ["Scripts"] = ProfilerCategory.Scripts, ["Internal"] = ProfilerCategory.Internal, ["Memory"] = ProfilerCategory.Memory, ["Render"] = ProfilerCategory.Render };
            if (!categories.TryGetValue(category, out var selected)) throw new ArgumentException("Unknown category.");
            var recorder = ProfilerRecorder.StartNew(selected, marker, Math.Max(1, Math.Min(4096, c.GetParam("capacity", 600))));
            if (!recorder.Valid) { recorder.Dispose(); throw new InvalidOperationException("Marker unavailable: register it first and verify profiling support."); }
            var id = Guid.NewGuid().ToString("N");
            _captures.Add(id, new Capture { Recorder = recorder, Marker = marker, Deadline = Time.realtimeSinceStartup + Math.Max(1, Math.Min(60, c.GetParam("durationSeconds", 10))) });
            return new { sessionId = id, marker, category, retention = "bounded ring buffer; release session after reading" };
        }
        private object Read(AIBridgeRuntimeCommand c, bool stop)
        {
            var id = c.GetParam<string>("sessionId");
            if (id == null || !_captures.TryGetValue(id, out var capture)) throw new ArgumentException("Unknown capture session.");
            if (stop) capture.Stop();
            var samples = capture.Stopped ? capture.Samples : capture.Recorder.ToArray();
            var result = new { sessionId = id, marker = capture.Marker, stopped = capture.Stopped,
                unit = "raw recorder unit: time markers nanoseconds, memory counters bytes",
                samples = samples.Select(s => new { value = s.Value, count = s.Count }).ToArray(),
                scope = "named marker samples, not a CPU hierarchy or GPU timeline" };
            if (stop && c.GetParam("release", true)) _captures.Remove(id);
            return result;
        }
        private sealed class Capture
        {
            internal ProfilerRecorder Recorder; internal string Marker; internal float Deadline; internal bool Stopped;
            internal ProfilerRecorderSample[] Samples = Array.Empty<ProfilerRecorderSample>();
            internal void Stop() { if (Stopped) return; try { Recorder.Stop(); Samples = Recorder.ToArray(); } finally { Recorder.Dispose(); Stopped = true; } }
        }
    }
}
