using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using System.Linq;
using System.Collections.Concurrent;
using UnityEditor.SceneManagement;
using System.Reflection;
using UnityEngine.UI;

namespace GeminiBridge.Editor
{
    [InitializeOnLoad]
    public static class GeminiBridgeServer
    {
        private static HttpListener _listener;
        private const int Port = 12121;
        private static bool _isRunning;
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private static readonly ConcurrentDictionary<string, Type> _typeCache = new ConcurrentDictionary<string, Type>();

        static GeminiBridgeServer()
        {
            EditorApplication.update += ProcessMainThreadQueue;
            StartServer();
        }

        private static void ProcessMainThreadQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError($"[GeminiBridge] Error executing main thread action: {e.Message}"); }
            }
        }

        [MenuItem("Gemini/Bridge/Restart Server")]
        public static void RestartServer()
        {
            StopServer();
            StartServer();
        }

        public static void StartServer()
        {
            if (_isRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Start();
                _isRunning = true;
                Task.Run(ListenLoop);
                Debug.Log($"[GeminiBridge] Server started on port {Port}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[GeminiBridge] Failed to start server: {e.Message}");
            }
        }

        public static void StopServer()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _listener?.Stop();
            _listener?.Close();
            Debug.Log("[GeminiBridge] Server stopped");
        }

        private static async Task ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = HandleRequest(context);
                }
                catch (HttpListenerException) { /* Server stopped */ }
                catch (Exception e)
                {
                    Debug.LogError($"[GeminiBridge] Error in listen loop: {e.Message}");
                }
            }
        }

        private static async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string path = request.Url.LocalPath.ToLower();
                byte[] buffer = Array.Empty<byte>();
                int statusCode = 200;

                try
                {
                    var completionSource = new TaskCompletionSource<(string json, int code)>();
                    string body = await GetRequestBody(request);
                    
                    _mainThreadQueue.Enqueue(() =>
                    {
                        try
                        {
                            var result = ProcessPath(path, request, body);
                            
                            // If result is an anonymous type with error, we might want to change status code
                            int code = 200;
                            var resultType = result?.GetType();
                            if (resultType != null && resultType.GetProperty("error") != null)
                            {
                                string err = resultType.GetProperty("error").GetValue(result)?.ToString();
                                if (err.Contains("not found") || err.Contains("Unknown path")) code = 404;
                                else if (err.Contains("required") || err.Contains("Invalid")) code = 400;
                                else code = 500;
                            }

                            string json = JsonConvert.SerializeObject(result, Formatting.Indented, new JsonSerializerSettings {
                                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                            });
                            completionSource.SetResult((json, code));
                        }
                        catch (Exception e)
                        {
                            var errorResult = new { error = e.Message, stack = e.StackTrace };
                            completionSource.SetResult((JsonConvert.SerializeObject(errorResult), 500));
                        }
                    });
                    
                    var timeoutTask = Task.Delay(15000);
                    var completedTask = await Task.WhenAny(completionSource.Task, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { error = "Request timed out waiting for Unity main thread." }));
                        statusCode = 504;
                    }
                    else
                    {
                        var res = await completionSource.Task;
                        buffer = Encoding.UTF8.GetBytes(res.json);
                        statusCode = res.code;
                    }
                }
                catch (Exception e)
                {
                    buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { error = e.Message }));
                    statusCode = 500;
                }

                response.StatusCode = statusCode;
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.KeepAlive = false;
                response.AddHeader("Access-Control-Allow-Origin", "*");
                
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GeminiBridge] Error handling request: {e.Message}");
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        private static async Task<string> GetRequestBody(HttpListenerRequest request)
        {
            if (!request.HasEntityBody) return null;
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        private static object ProcessPath(string path, HttpListenerRequest request, string body = null)
        {
            var bodyParams = string.IsNullOrEmpty(body) ? new Dictionary<string, string>() : 
                JsonConvert.DeserializeObject<Dictionary<string, string>>(body) ?? new Dictionary<string, string>();

            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);

            switch (path)
            {
                case "/ping":
                    return new { 
                        status = "ok", 
                        version = "1.5.0", 
                        project = Application.productName, 
                        platform = Application.platform.ToString(),
                        unityVersion = Application.unityVersion
                    };

                case "/check_errors":
                    return new { compilationFailed = EditorUtility.scriptCompilationFailed };

                case "/hierarchy":
                    bool full = GetParam("full") == "true";
                    return GetHierarchy(full);

                case "/inspector":
                    if (int.TryParse(GetParam("id"), out int id)) return GetObjectDetails(id);
                    return new { error = "ID required" };

                case "/assets":
                    return SearchAssets(GetParam("filter") ?? "");

                case "/execute":
                    return ExecuteCommand(GetParam("cmd"), request, bodyParams);
            
                case "/modify":
                    return ModifyObject(request, bodyParams);

                case "/create":
                    return CreateObject(request, bodyParams);

                case "/delete":
                    if (int.TryParse(GetParam("id"), out int delId)) return DeleteObject(delId);
                    return new { error = "ID required" };

                case "/call":
                    return CallMethod(GetParam("type"), GetParam("method"));

                case "/system":
                    return new { 
                        os = SystemInfo.operatingSystem, 
                        cpu = SystemInfo.processorType, 
                        ram = SystemInfo.systemMemorySize, 
                        gpu = SystemInfo.graphicsDeviceName,
                        vram = SystemInfo.graphicsMemorySize,
                        graphicsApi = SystemInfo.graphicsDeviceType.ToString()
                    };

                case "/logs":
                    int count = 10; int.TryParse(GetParam("count"), out count);
                    return GetLogs(count);

                case "/diagnostics":
                    return RunDiagnostics();

                case "/screenshot":
                    return CaptureScreenshot();

                case "/component":
                    return HandleComponent(request, bodyParams);

                case "/scene":
                    return HandleScene(request, bodyParams);

                case "/selection":
                    return HandleSelection(request, bodyParams);

                case "/asset":
                    return HandleAsset(request, bodyParams);

                case "/asset_info":
                    return GetAssetInfo(GetParam("path"));

                case "/transform":
                    return HandleTransform(request, bodyParams);

                case "/prefab":
                    return HandlePrefab(request, bodyParams);

                case "/editor":
                    return HandleEditor(request, bodyParams);

                default:
                    return new { error = $"Unknown path: {path}" };
            }
        }

        private static object GetAssetInfo(string path)
        {
            if (string.IsNullOrEmpty(path)) return new { error = "Path required" };
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return new { error = "Asset not found" };
            
            var deps = AssetDatabase.GetDependencies(path, false);
            var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
            
            long fileSize = 0;
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath)) fileSize = new FileInfo(fullPath).Length;

            return new {
                path = path,
                guid = guid,
                type = mainType?.FullName,
                dependencies = deps,
                size = fileSize
            };
        }

        private static object HandleComponent(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            
            if (!int.TryParse(GetParam("id"), out int id)) return new { error = "ID required" };
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return new { error = "Object not found" };

            string action = GetParam("action");
            string typeName = GetParam("type");
            string propName = GetParam("name");
            string value = GetParam("value");

            switch (action)
            {
                case "add":
                    var t = FindType(typeName);
                    if (t == null) return new { error = "Type not found" };
                    var comp = Undo.AddComponent(go, t);
                    return new { status = "Added", type = t.FullName };

                case "remove":
                    var tRem = FindType(typeName);
                    if (tRem == null) return new { error = "Type not found" };
                    var cRem = go.GetComponent(tRem);
                    if (cRem == null) return new { error = "Component not found on object" };
                    Undo.DestroyObjectImmediate(cRem);
                    return new { status = "Removed" };

                case "set":
                    var tSet = FindType(typeName);
                    if (tSet == null) return new { error = "Type not found" };
                    var cSet = go.GetComponent(tSet);
                    if (cSet == null) return new { error = "Component not found" };
                    if (SetPropertyValue(cSet, propName, value))
                    {
                        EditorUtility.SetDirty(cSet);
                        return new { status = "OK", value = value };
                    }
                    return new { error = $"Failed to set property {propName}" };

                case "get":
                    var tGet = FindType(typeName);
                    if (tGet == null) return new { error = "Type not found" };
                    var cGet = go.GetComponent(tGet);
                    if (cGet == null) return new { error = "Component not found" };
                    return new { value = GetPropertyValue(cGet, propName) };

                case "invoke":
                    var tInv = FindType(typeName);
                    if (tInv == null) return new { error = "Type not found" };
                    var cInv = go.GetComponent(tInv);
                    if (cInv == null) return new { error = "Component not found" };
                    return InvokeMethod(cInv, GetParam("method"), GetParam("args"));
            }
            return new { error = "Invalid component action" };
        }

        private static object HandleScene(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            string action = GetParam("action");
            switch (action)
            {
                case "new":
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
                    return new { status = "Created" };
                case "save":
                    EditorSceneManager.SaveOpenScenes();
                    return new { status = "Saved" };
                case "open":
                    string path = GetParam("path");
                    if (string.IsNullOrEmpty(path)) return new { error = "Path required" };
                    EditorSceneManager.OpenScene(path);
                    return new { status = "Opened", scene = path };
                case "focus":
                    if (int.TryParse(GetParam("id"), out int id))
                    {
                        var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                        if (go == null) return new { error = "Not found" };
                        Selection.activeObject = go;
                        SceneView.lastActiveSceneView.FrameSelected();
                        return new { status = "Focused" };
                    }
                    return new { error = "ID required" };
                case "duplicate":
                    if (int.TryParse(GetParam("id"), out int dupId))
                    {
                        var go = EditorUtility.InstanceIDToObject(dupId) as GameObject;
                        if (go == null) return new { error = "Not found" };
                        var dup = GameObject.Instantiate(go);
                        Undo.RegisterCreatedObjectUndo(dup, "Duplicate");
                        return new { id = dup.GetInstanceID(), name = dup.name };
                    }
                    return new { error = "ID required" };
                case "set_parent":
                    if (int.TryParse(GetParam("id"), out int childId) && int.TryParse(GetParam("parent"), out int parentId))
                    {
                        var child = EditorUtility.InstanceIDToObject(childId) as GameObject;
                        var parent = parentId == 0 ? null : EditorUtility.InstanceIDToObject(parentId) as GameObject;
                        if (child == null) return new { error = "Child not found" };
                        Undo.SetTransformParent(child.transform, parent?.transform, "Set Parent");
                        return new { status = "Parent updated" };
                    }
                    return new { error = "ID and ParentID required" };
            }
            return new { error = "Invalid scene action" };
        }

        private static object HandleSelection(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            string action = GetParam("action");
            switch (action)
            {
                case "get":
                    return Selection.instanceIDs;
                case "set":
                    string idsRaw = GetParam("ids");
                    if (string.IsNullOrEmpty(idsRaw)) return new { error = "IDs required" };
                    Selection.instanceIDs = idsRaw.Split(',').Select(int.Parse).ToArray();
                    return new { status = "Selection updated" };
                case "clear":
                    Selection.activeObject = null;
                    return new { status = "Selection cleared" };
            }
            return new { error = "Invalid selection action" };
        }

        private static object HandleAsset(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            string action = GetParam("action");
            switch (action)
            {
                case "create_material":
                    string path = GetParam("path") ?? "Assets/NewMaterial.mat";
                    string shader = GetParam("shader") ?? "Standard";
                    Material mat = new Material(Shader.Find(shader));
                    AssetDatabase.CreateAsset(mat, path);
                    return new { status = "Created", path = path };
                case "apply_material":
                    if (int.TryParse(GetParam("id"), out int id))
                    {
                        string matPath = GetParam("path");
                        var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                        var renderer = go?.GetComponent<Renderer>();
                        var material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                        if (renderer != null && material != null)
                        {
                            Undo.RecordObject(renderer, "Apply Material");
                            renderer.sharedMaterial = material;
                            return new { status = "Applied" };
                        }
                    }
                    return new { error = "ID and Path required" };
                case "rename":
                    string rPath = GetParam("path");
                    string rName = GetParam("name");
                    string rRes = AssetDatabase.RenameAsset(rPath, rName);
                    return string.IsNullOrEmpty(rRes) ? new { status = "Renamed" } : new { error = rRes };
                case "move":
                    string mPath = GetParam("from");
                    string mDest = GetParam("to");
                    string mRes = AssetDatabase.MoveAsset(mPath, mDest);
                    return string.IsNullOrEmpty(mRes) ? new { status = "Moved" } : new { error = mRes };
                case "delete":
                    string delPath = GetParam("path");
                    if (AssetDatabase.MoveAssetToTrash(delPath)) return new { status = "Deleted" };
                    return new { error = "Delete failed" };
                case "import":
                    AssetDatabase.ImportAsset(GetParam("path"));
                    return new { status = "Imported" };
                case "find_by_type":
                    string fType = GetParam("type");
                    return AssetDatabase.FindAssets($"t:{fType}")
                        .Select(guid => AssetDatabase.GUIDToAssetPath(guid)).ToList();
            }
            return new { error = "Invalid asset action" };
        }

        private static object HandleTransform(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            if (!int.TryParse(GetParam("id"), out int id)) return new { error = "ID required" };
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return new { error = "Not found" };

            string action = GetParam("action");
            switch (action)
            {
                case "look_at":
                    if (int.TryParse(GetParam("target"), out int targetId))
                    {
                        var target = EditorUtility.InstanceIDToObject(targetId) as GameObject;
                        if (target != null) { Undo.RecordObject(go.transform, "LookAt"); go.transform.LookAt(target.transform); return new { status = "OK" }; }
                    }
                    else if (TryParseVector3(GetParam("target"), out Vector3 point))
                    {
                        Undo.RecordObject(go.transform, "LookAt"); go.transform.LookAt(point); return new { status = "OK" };
                    }
                    break;
                case "align_with_view":
                    Selection.activeGameObject = go;
                    SceneView.lastActiveSceneView.AlignWithView();
                    return new { status = "Aligned" };
                case "reset":
                    Undo.RecordObject(go.transform, "Reset Transform");
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    return new { status = "Reset" };
            }
            return new { error = "Invalid transform action" };
        }

        private static object HandlePrefab(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            string action = GetParam("action");
            string path = GetParam("path");

            switch (action)
            {
                case "load":
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) return new { error = "Prefab not found" };
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    Undo.RegisterCreatedObjectUndo(inst, "Load Prefab");
                    return new { id = inst.GetInstanceID(), name = inst.name };
                case "save":
                    if (int.TryParse(GetParam("id"), out int id))
                    {
                        var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                        PrefabUtility.SaveAsPrefabAsset(go, path);
                        return new { status = "Saved", path = path };
                    }
                    break;
                case "unpack":
                    if (int.TryParse(GetParam("id"), out int upId))
                    {
                        var go = EditorUtility.InstanceIDToObject(upId) as GameObject;
                        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.UserAction);
                        return new { status = "Unpacked" };
                    }
                    break;
            }
            return new { error = "Invalid prefab action" };
        }

        private static object HandleEditor(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            string action = GetParam("action");
            switch (action)
            {
                case "window_open":
                    string type = GetParam("type");
                    var wType = FindType(type);
                    if (wType != null) { EditorWindow.GetWindow(wType); return new { status = "Opened" }; }
                    return new { error = "Window type not found" };
                case "notification":
                    SceneView.lastActiveSceneView.ShowNotification(new GUIContent(GetParam("msg") ?? "Gemini Message"));
                    return new { status = "OK" };
                case "set_view":
                    if (TryParseVector3(GetParam("pos"), out Vector3 pos)) SceneView.lastActiveSceneView.pivot = pos;
                    if (TryParseVector3(GetParam("rot"), out Vector3 rot)) SceneView.lastActiveSceneView.rotation = Quaternion.Euler(rot);
                    SceneView.lastActiveSceneView.Repaint();
                    return new { status = "View updated" };
            }
            return new { error = "Invalid editor action" };
        }

        private static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_typeCache.TryGetValue(name, out var type)) return type;

            type = Type.GetType(name);
            if (type == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = a.GetType(name) ?? a.GetTypes().FirstOrDefault(t => t.Name == name || t.FullName == name);
                    if (type != null) break;
                }
            }

            if (type != null) _typeCache[name] = type;
            return type;
        }

        private static object GetPropertyValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrEmpty(memberName)) return null;
            var type = target.GetType();
            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(target) ?? prop?.GetValue(target, null);
        }

        private static bool SetPropertyValue(object target, string memberName, string value)
        {
            if (target == null || string.IsNullOrEmpty(memberName)) return false;
            
            Type type = target.GetType();
            FieldInfo field = null;
            PropertyInfo prop = null;

            while (type != null)
            {
                field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) break;

                prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null) break;

                type = type.BaseType;
            }
            
            Type memberType = field?.FieldType ?? prop?.PropertyType;
            if (memberType == null) return false;

            object val = ParseValue(value, memberType);
            if (val == null && memberType != typeof(string)) return false;

            if (field != null) { Undo.RecordObject((UnityEngine.Object)target, "Set field"); field.SetValue(target, val); }
            else { Undo.RecordObject((UnityEngine.Object)target, "Set property"); prop.SetValue(target, val, null); }
            return true;
        }

        private static object InvokeMethod(object target, string methodName, string argsJson)
        {
            if (target == null || string.IsNullOrEmpty(methodName)) return new { error = "Invalid target or method" };
            var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return new { error = "Method not found" };

            object[] args = null;
            if (!string.IsNullOrEmpty(argsJson))
            {
                var argList = JsonConvert.DeserializeObject<List<string>>(argsJson);
                var parameters = method.GetParameters();
                args = new object[parameters.Length];
                for (int i = 0; i < Math.Min(parameters.Length, argList.Count); i++)
                    args[i] = ParseValue(argList[i], parameters[i].ParameterType);
            }

            var result = method.Invoke(target, args);
            return new { status = "Invoked", result = result?.ToString() };
        }

        private static object ParseValue(string value, Type targetType)
        {
            if (targetType == typeof(string)) return value;
            if (targetType == typeof(int)) return int.TryParse(value, out int i) ? i : 0;
            if (targetType == typeof(float)) return float.TryParse(value, out float f) ? f : 0f;
            if (targetType == typeof(double)) return double.TryParse(value, out double d) ? d : 0.0;
            if (targetType == typeof(bool)) return value.ToLower() == "true";
            
            // Handle JSON objects for Vector/Color
            if (value.Trim().StartsWith("{"))
            {
                try { return JsonConvert.DeserializeObject(value, targetType); } catch { }
            }

            if (targetType == typeof(Vector2)) { if (TryParseVector3(value, out Vector3 v2)) return new Vector2(v2.x, v2.y); }
            if (targetType == typeof(Vector3) && TryParseVector3(value, out Vector3 v3)) return v3;
            if (targetType == typeof(Vector4)) { var p = value.Split(','); if (p.Length == 4) return new Vector4(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3])); }
            if (targetType == typeof(Quaternion)) { var p = value.Split(','); if (p.Length == 4) return new Quaternion(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3])); }
            if (targetType == typeof(Color)) {
                if (ColorUtility.TryParseHtmlString(value, out Color c)) return c;
                if (TryParseVector3(value, out Vector3 cv)) return new Color(cv.x, cv.y, cv.z);
            }
            if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType) && int.TryParse(value, out int id)) return EditorUtility.InstanceIDToObject(id);
            return null;
        }

        private static object GetHierarchy(bool full)
        {
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            return roots.Select(go => SerializeGameObject(go, full)).ToList();
        }

        private static object SerializeGameObject(GameObject go, bool full)
        {
            return new
            {
                id = go.GetInstanceID(),
                name = go.name,
                active = go.activeSelf,
                tag = go.tag,
                layer = go.layer,
                components = go.GetComponents<Component>().Where(c => c != null).Select(c => full ? SerializeComponentFull(c) : (object)c.GetType().Name).ToList(),
                children = go.transform.Cast<Transform>().Select(t => SerializeGameObject(t.gameObject, full)).ToList()
            };
        }

        private static object SerializeComponentFull(Component c)
        {
            return new {
                type = c.GetType().FullName,
                data = SerializeComponent(c)
            };
        }

        private static object GetObjectDetails(int instanceId)
        {
            var obj = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (obj == null) return new { error = "Object not found" };

            return new
            {
                id = instanceId,
                name = obj.name,
                components = obj.GetComponents<Component>().Where(c => c != null).Select(c => new {
                    type = c.GetType().FullName,
                    enabled = (c as MonoBehaviour)?.enabled ?? true,
                    data = SerializeComponent(c)
                }).ToList()
            };
        }

        private static object SerializeComponent(Component c)
        {
            if (c == null) return null;
            var data = new Dictionary<string, object>();
            var so = new SerializedObject(c);
            var prop = so.GetIterator();
            bool next = prop.NextVisible(true);
            while (next)
            {
                data[prop.name] = GetSerializedPropertyValue(prop);
                next = prop.NextVisible(false);
            }
            return data;
        }

        private static object GetSerializedPropertyValue(SerializedProperty prop)
        {
            return prop.propertyType switch
            {
                SerializedPropertyType.Integer => prop.intValue,
                SerializedPropertyType.Boolean => prop.boolValue,
                SerializedPropertyType.Float => prop.floatValue,
                SerializedPropertyType.String => prop.stringValue,
                SerializedPropertyType.Color => prop.colorValue,
                SerializedPropertyType.ObjectReference => prop.objectReferenceValue ? prop.objectReferenceValue.name : "null",
                SerializedPropertyType.LayerMask => prop.intValue,
                SerializedPropertyType.Enum => prop.enumDisplayNames.Length > 0 ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString(),
                SerializedPropertyType.Vector2 => prop.vector2Value,
                SerializedPropertyType.Vector3 => prop.vector3Value,
                SerializedPropertyType.Vector4 => prop.vector4Value,
                SerializedPropertyType.Rect => prop.rectValue,
                SerializedPropertyType.ArraySize => prop.intValue,
                SerializedPropertyType.Character => ((char)prop.intValue).ToString(),
                SerializedPropertyType.AnimationCurve => "AnimationCurve",
                SerializedPropertyType.Bounds => prop.boundsValue,
                SerializedPropertyType.Quaternion => prop.quaternionValue,
                SerializedPropertyType.Vector2Int => prop.vector2IntValue,
                SerializedPropertyType.Vector3Int => prop.vector3IntValue,
                _ => null
            };
        }

        private static object SearchAssets(string filter)
        {
            return AssetDatabase.FindAssets(filter)
                .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                .Select(path => new { path, type = AssetDatabase.GetMainAssetTypeAtPath(path)?.Name })
                .ToList();
        }

        private static object ModifyObject(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            if (!int.TryParse(GetParam("id"), out int id)) return new { error = "ID required" };
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return new { error = "Not found" };

            string action = GetParam("action");
            string val = GetParam("value");

            switch (action)
            {
                case "rename": go.name = val; return new { status = "OK" };
                case "active": go.SetActive(val == "true"); return new { status = "OK" };
                case "set_pos": 
                    if (TryParseVector3(val, out Vector3 p)) { Undo.RecordObject(go.transform, "Move"); go.transform.localPosition = p; return new { status = "OK" }; }
                    break;
                case "set_layer":
                    if (int.TryParse(val, out int layer)) { Undo.RecordObject(go, "Set Layer"); go.layer = layer; return new { status = "OK" }; }
                    break;
                case "set_tag":
                    Undo.RecordObject(go, "Set Tag"); go.tag = val; return new { status = "OK" };
            }
            return new { error = "Invalid action" };
        }

        private static object CreateObject(HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            string type = GetParam("type") ?? "empty";
            GameObject go = type.ToLower() switch {
                "cube" => GameObject.CreatePrimitive(PrimitiveType.Cube),
                "sphere" => GameObject.CreatePrimitive(PrimitiveType.Sphere),
                "capsule" => GameObject.CreatePrimitive(PrimitiveType.Capsule),
                "cylinder" => GameObject.CreatePrimitive(PrimitiveType.Cylinder),
                "plane" => GameObject.CreatePrimitive(PrimitiveType.Plane),
                "light" => new GameObject("Light", typeof(Light)),
                "camera" => new GameObject("Camera", typeof(Camera)),
                "ui_canvas" => new GameObject("Canvas", typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster)),
                _ => new GameObject("New Object")
            };
            Undo.RegisterCreatedObjectUndo(go, "Create");
            return new { id = go.GetInstanceID(), name = go.name };
        }

        private static object DeleteObject(int id)
        {
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return new { error = "Not found" };
            Undo.DestroyObjectImmediate(go);
            return new { status = "Deleted" };
        }

        private static object CallMethod(string typeName, string methodName)
        {
            try {
                var type = FindType(typeName);
                if (type == null) return new { error = "Type not found" };
                var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method == null) return new { error = "Method not found" };
                return new { status = "OK", result = method.Invoke(null, null)?.ToString() };
            } catch (Exception e) { return new { error = e.Message }; }
        }

        private static object GetLogs(int count)
        {
            try {
                var assembly = typeof(EditorWindow).Assembly;
                var type = assembly.GetType("UnityEditor.LogEntries");
                if (type == null) return new { error = "LogEntries type not found" };

                var getCountMethod = type.GetMethod("GetCount");
                if (getCountMethod == null) return new { error = "GetCount method not found" };
                int total = (int)getCountMethod.Invoke(null, null);

                var entryType = assembly.GetType("UnityEditor.LogEntry");
                if (entryType == null) return new { error = "LogEntry type not found" };
                var entry = Activator.CreateInstance(entryType);
                var getter = type.GetMethod("GetEntryInternal");
                
                var results = new List<object>();
                var conditionField = entryType.GetField("condition");
                var modeField = entryType.GetField("mode");

                for (int i = Mathf.Max(0, total - count); i < total; i++) {
                    getter.Invoke(null, new[] { i, entry });
                    results.Add(new { 
                        msg = conditionField?.GetValue(entry),
                        type = modeField?.GetValue(entry)?.ToString()
                    });
                }
                return results;
            } catch (Exception e) { return new { error = e.Message }; }
        }

        private static object RunDiagnostics()
        {
#if UNITY_2023_1_OR_NEWER
            var all = GameObject.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var all = GameObject.FindObjectsOfType<GameObject>(true);
#endif
            var missing = all.Where(go => go.GetComponents<Component>().Any(c => c == null))
                             .Select(go => new { id = go.GetInstanceID(), name = go.name }).ToList();
            return new { missingScripts = missing, checkedCount = all.Length };
        }

        private static object ExecuteCommand(string cmd, HttpListenerRequest request, Dictionary<string, string> bodyParams)
        {
            string GetParam(string name) => request.QueryString[name] ?? (bodyParams.TryGetValue(name, out var val) ? val : null);
            switch (cmd?.ToLower()) {
                case "play": EditorApplication.isPlaying = true; break;
                case "stop": EditorApplication.isPlaying = false; break;
                case "refresh": AssetDatabase.Refresh(); break;
                case "select": 
                    if (int.TryParse(GetParam("id"), out int id)) Selection.activeObject = EditorUtility.InstanceIDToObject(id);
                    break;
            }
            return new { status = "Executed" };
        }

        private static object CaptureScreenshot()
        {
            string path = "gemini_screenshot.png";
            ScreenCapture.CaptureScreenshot(path);
            
            // Note: CaptureScreenshot is asynchronous and happens after the frame.
            // Returning base64 immediately might return old data or fail if we read too early.
            // However, for simplicity in this bridge, we'll try to read it if it exists or return the path.
            // A better way would be using a Coroutine and RenderTexture, but that's more complex for this server.
            return new { 
                status = "Captured", 
                path = Path.GetFullPath(path),
                info = "Screenshot saved to project root. For direct base64, use a separate specialized endpoint if implemented via RenderTexture."
            };
        }

        private static bool TryParseVector3(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split(',');
            if (p.Length >= 2)
            {
                if (float.TryParse(p[0], out v.x) && float.TryParse(p[1], out v.y))
                {
                    if (p.Length >= 3 && float.TryParse(p[2], out v.z)) return true;
                    return true;
                }
            }
            return false;
        }
    }
}