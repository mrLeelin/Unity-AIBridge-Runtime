using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AIBridge.Runtime.Shared
{
    public static class AIBridgeUiAutomation
    {
        private const int DefaultMaxResults = 100;
        private const int DefaultRaycastMaxResults = 20;
        private const int MaxResults = 500;

        public static object Snapshot(int maxResults = 100, bool includeDisabled = false)
        {
            var buttons = CollectButtons(null, includeDisabled, ResolveMaxResults(maxResults, DefaultMaxResults), true);
            var canvases = BuildCanvasInfos(buttons.countsByCanvasPath, includeDisabled);
            return new
            {
                action = "ui.snapshot",
                screen = BuildScreenInfo(),
                eventSystem = BuildEventSystemInfo(),
                selected = BuildGameObjectInfo(GetSelectedGameObject()),
                canvases = canvases,
                buttons = buttons.entries.ToArray(),
                summary = new
                {
                    canvasCount = canvases.Length,
                    buttonCount = buttons.totalCount,
                    returnedButtonCount = buttons.entries.Count,
                    clickableButtonCount = CountClickableButtons(buttons.entries),
                    truncated = buttons.truncated,
                    hasEventSystem = EventSystem.current != null
                }
            };
        }

        public static object Find(string keyword, int maxResults = 100, bool includeDisabled = false)
        {
            var buttons = CollectButtons(keyword, includeDisabled, ResolveMaxResults(maxResults, DefaultMaxResults), true);
            return new
            {
                action = "ui.find",
                keyword = keyword,
                buttons = buttons.entries.ToArray(),
                summary = new
                {
                    matchCount = buttons.totalCount,
                    returnedCount = buttons.entries.Count,
                    clickableCount = CountClickableButtons(buttons.entries),
                    truncated = buttons.truncated
                }
            };
        }

        public static object Raycast(float x, float y, string path = null, int instanceId = 0, int maxResults = 20)
        {
            var target = ResolveTarget(path, instanceId);
            if ((!string.IsNullOrEmpty(path) || instanceId != 0) && target == null)
            {
                return new { action = "ui.raycast", success = false, error = "Requested target was not found." };
            }

            string pointSource;
            string error;
            Vector2 point;
            if (!TryResolveScreenPoint(x, y, target, out point, out pointSource, out error))
            {
                return new
                {
                    action = "ui.raycast",
                    success = false,
                    error = error,
                    requestedTarget = BuildGameObjectInfo(target)
                };
            }

            List<RaycastResult> hits;
            if (!TryRaycast(point, out hits, out error))
            {
                return new
                {
                    action = "ui.raycast",
                    success = false,
                    error = error,
                    point = BuildVector2Info(point),
                    pointSource = pointSource,
                    requestedTarget = BuildGameObjectInfo(target)
                };
            }

            var truncated = TrimHits(hits, ResolveMaxResults(maxResults, DefaultRaycastMaxResults));
            return new
            {
                action = "ui.raycast",
                success = true,
                point = BuildVector2Info(point),
                pointSource = pointSource,
                requestedTarget = BuildGameObjectInfo(target),
                hitCount = hits.Count,
                truncated = truncated,
                hits = BuildRaycastInfos(hits),
                topHit = hits.Count > 0 ? BuildRaycastInfo(hits[0], 0) : null
            };
        }

        public static object Click(float x, float y, string path = null, int instanceId = 0)
        {
            var target = ResolveTarget(path, instanceId);
            if ((!string.IsNullOrEmpty(path) || instanceId != 0) && target == null)
            {
                return new { action = "ui.click", success = false, error = "Requested target was not found." };
            }

            string pointSource;
            string error;
            Vector2 point;
            if (!TryResolveScreenPoint(x, y, target, out point, out pointSource, out error))
            {
                return new
                {
                    action = "ui.click",
                    success = false,
                    error = error,
                    requestedTarget = BuildGameObjectInfo(target)
                };
            }

            var selectedBefore = GetSelectedGameObject();
            UiPointerState state;
            if (!TryClickAtScreenPoint(point, target, out state, out error))
            {
                return new
                {
                    action = "ui.click",
                    success = false,
                    error = error,
                    point = BuildVector2Info(point),
                    pointSource = pointSource,
                    requestedTarget = BuildGameObjectInfo(target),
                    selectedBefore = BuildGameObjectInfo(selectedBefore)
                };
            }

            return new
            {
                action = "ui.click",
                success = true,
                point = BuildVector2Info(point),
                pointSource = pointSource,
                requestedTarget = BuildGameObjectInfo(target),
                selectedBefore = BuildGameObjectInfo(selectedBefore),
                selectedAfter = BuildGameObjectInfo(GetSelectedGameObject()),
                hitCount = state.raycastResults.Count,
                hits = BuildRaycastInfos(state.raycastResults),
                raycastTarget = BuildGameObjectInfo(state.raycastTarget),
                pointerPress = BuildGameObjectInfo(state.pointerPress),
                clickHandler = BuildGameObjectInfo(state.clickHandler),
                clicked = state.pointerPress != null && state.clickHandler != null && state.pointerPress == state.clickHandler,
                topHit = state.raycastResults.Count > 0 ? BuildRaycastInfo(state.raycastResults[0], 0) : null
            };
        }

        private static ButtonCollection CollectButtons(string keyword, bool includeDisabled, int maxResults, bool includeRaycastDetails)
        {
            var buttons = FindSceneObjects<Button>(includeDisabled);
            var result = new ButtonCollection();
            var canRaycast = includeRaycastDetails && EventSystem.current != null;
            for (var i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                if (button == null || button.gameObject == null)
                {
                    continue;
                }

                if (!includeDisabled && (!button.gameObject.activeInHierarchy || !button.enabled || !button.IsInteractable()))
                {
                    continue;
                }

                var entry = BuildButtonInfo(button);
                if (!MatchesKeyword(entry, keyword))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.canvasPath))
                {
                    int count;
                    result.countsByCanvasPath.TryGetValue(entry.canvasPath, out count);
                    result.countsByCanvasPath[entry.canvasPath] = count + 1;
                }

                if (canRaycast && entry.screenPointAvailable)
                {
                    string raycastError;
                    List<RaycastResult> hits;
                    if (TryRaycast(new Vector2(entry.clickPoint.x, entry.clickPoint.y), out hits, out raycastError))
                    {
                        entry.raycastAvailable = true;
                        entry.raycastCount = hits.Count;
                        entry.raycastIndex = IndexOfGameObjectInRaycast(button.gameObject, hits);
                        entry.raycastTarget = hits.Count > 0 ? BuildGameObjectInfo(hits[0].gameObject) : null;
                        entry.topmost = entry.raycastIndex == 0;
                    }
                    else
                    {
                        entry.raycastError = raycastError;
                    }
                }

                entry.clickable = button.IsInteractable()
                    && entry.screenPointAvailable
                    && (!entry.raycastAvailable || entry.raycastIndex == 0);
                result.entries.Add(entry);
                result.totalCount++;
            }

            result.entries.Sort(CompareButtonInfos);
            if (result.entries.Count > maxResults)
            {
                result.truncated = true;
                result.entries.RemoveRange(maxResults, result.entries.Count - maxResults);
            }

            return result;
        }

        private static ButtonInfo BuildButtonInfo(Button button)
        {
            var entry = new ButtonInfo
            {
                name = button.name,
                label = ExtractButtonLabel(button),
                path = BuildTransformPath(button.transform),
                instanceId = button.gameObject.GetInstanceID(),
                activeInHierarchy = button.gameObject.activeInHierarchy,
                enabled = button.enabled,
                interactable = button.IsInteractable(),
                raycastIndex = -1
            };

            var canvas = button.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                entry.canvasPath = BuildTransformPath(canvas.transform);
                entry.canvasName = canvas.name;
                entry.canvasSortingOrder = canvas.sortingOrder;
                entry.canvasRenderMode = canvas.renderMode.ToString();
            }

            Vector2 point;
            Rect rect;
            string error;
            if (TryGetScreenMetrics(button.transform as RectTransform, out point, out rect, out error))
            {
                entry.screenPointAvailable = true;
                entry.clickPoint = BuildVector2Info(point);
                entry.screenRect = BuildRectInfo(rect);
                entry.screenRectAvailable = true;
            }
            else
            {
                entry.screenMetricError = error;
            }

            entry.clickable = button.IsInteractable() && entry.screenPointAvailable;
            return entry;
        }

        private static object[] BuildCanvasInfos(Dictionary<string, int> countsByCanvasPath, bool includeDisabled)
        {
            var canvases = FindSceneObjects<Canvas>(includeDisabled);
            var entries = new List<object>();
            for (var i = 0; i < canvases.Length; i++)
            {
                var canvas = canvases[i];
                if (canvas == null || canvas.gameObject == null)
                {
                    continue;
                }

                if (!includeDisabled && (!canvas.enabled || !canvas.gameObject.activeInHierarchy))
                {
                    continue;
                }

                var path = BuildTransformPath(canvas.transform);
                int buttonCount;
                countsByCanvasPath.TryGetValue(path, out buttonCount);
                entries.Add(new
                {
                    name = canvas.name,
                    path = path,
                    instanceId = canvas.gameObject.GetInstanceID(),
                    enabled = canvas.enabled,
                    activeInHierarchy = canvas.gameObject.activeInHierarchy,
                    isRootCanvas = canvas.isRootCanvas,
                    renderMode = canvas.renderMode.ToString(),
                    sortingLayerId = canvas.sortingLayerID,
                    sortingLayerName = SortingLayer.IDToName(canvas.sortingLayerID),
                    sortingOrder = canvas.sortingOrder,
                    overrideSorting = canvas.overrideSorting,
                    scaleFactor = canvas.scaleFactor,
                    referencePixelsPerUnit = canvas.referencePixelsPerUnit,
                    pixelRect = BuildRectInfo(canvas.pixelRect),
                    worldCamera = canvas.worldCamera == null ? null : BuildGameObjectInfo(canvas.worldCamera.gameObject),
                    buttonCount = buttonCount,
                    hasGraphicRaycaster = canvas.GetComponent<GraphicRaycaster>() != null
                });
            }

            return entries.ToArray();
        }

        private static bool TryResolveScreenPoint(float x, float y, GameObject target, out Vector2 point, out string source, out string error)
        {
            point = Vector2.zero;
            source = null;
            error = null;

            if (target != null && TryResolveTargetScreenPoint(target, out point, out source, out error))
            {
                return true;
            }

            point = new Vector2(x, y);
            source = "screen";
            return true;
        }

        private static bool TryResolveTargetScreenPoint(GameObject target, out Vector2 point, out string source, out string error)
        {
            point = Vector2.zero;
            source = null;
            error = null;

            Rect rect;
            if (TryGetScreenMetrics(target.transform as RectTransform, out point, out rect, out error))
            {
                source = "target";
                return true;
            }

            var renderer = target.GetComponentInChildren<Renderer>();
            if (renderer != null && TryWorldToScreenPoint(renderer.bounds.center, out point, out error))
            {
                source = "renderer";
                return true;
            }

            var collider = target.GetComponentInChildren<Collider>();
            if (collider != null && TryWorldToScreenPoint(collider.bounds.center, out point, out error))
            {
                source = "collider";
                return true;
            }

            if (TryWorldToScreenPoint(target.transform.position, out point, out error))
            {
                source = "transform";
                return true;
            }

            if (string.IsNullOrEmpty(error))
            {
                error = "Unable to project target to screen coordinates.";
            }

            return false;
        }

        private static bool TryClickAtScreenPoint(Vector2 point, GameObject fallbackTarget, out UiPointerState state, out string error)
        {
            state = null;
            error = null;

            List<RaycastResult> hits;
            if (!TryRaycast(point, out hits, out error))
            {
                return false;
            }

            var raycastTarget = GetFirstRaycastTarget(hits);
            var eventTarget = raycastTarget != null ? raycastTarget : fallbackTarget;
            if (eventTarget == null)
            {
                error = "No EventSystem raycast target found at the requested screen position.";
                return false;
            }

            var data = CreatePointerEventData(point);
            if (hits.Count > 0)
            {
                data.pointerCurrentRaycast = hits[0];
                data.pointerPressRaycast = hits[0];
            }

            data.rawPointerPress = eventTarget;
            data.pressPosition = point;
            data.position = point;
            data.eligibleForClick = true;
            data.clickTime = Time.unscaledTime;
            data.clickCount = 1;
            data.useDragThreshold = true;
            data.pointerEnter = eventTarget;

            var selected = ExecuteEvents.GetEventHandler<ISelectHandler>(eventTarget);
            if (selected != null && EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(selected, data);
            }

            var pointerPress = ExecuteEvents.ExecuteHierarchy(eventTarget, data, ExecuteEvents.pointerDownHandler);
            if (pointerPress == null)
            {
                pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            }

            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            if (pointerPress == null && clickHandler == null)
            {
                error = "No pointer click handler found for target.";
                return false;
            }

            data.pointerPress = pointerPress;
            state = new UiPointerState
            {
                raycastTarget = raycastTarget,
                pointerPress = pointerPress,
                clickHandler = clickHandler,
                raycastResults = hits
            };

            if (pointerPress != null)
            {
                ExecuteEvents.Execute(pointerPress, data, ExecuteEvents.pointerUpHandler);
            }

            if (pointerPress != null && clickHandler != null && pointerPress == clickHandler && data.eligibleForClick)
            {
                ExecuteEvents.Execute(pointerPress, data, ExecuteEvents.pointerClickHandler);
            }

            data.eligibleForClick = false;
            data.pointerPress = null;
            data.rawPointerPress = null;
            data.dragging = false;
            return true;
        }

        private static bool TryRaycast(Vector2 point, out List<RaycastResult> hits, out string error)
        {
            hits = new List<RaycastResult>();
            error = null;
            if (EventSystem.current == null)
            {
                error = "UI raycast requires an active EventSystem.";
                return false;
            }

            EventSystem.current.RaycastAll(CreatePointerEventData(point), hits);
            return true;
        }

        private static PointerEventData CreatePointerEventData(Vector2 point)
        {
            return new PointerEventData(EventSystem.current)
            {
                pointerId = -1,
                button = PointerEventData.InputButton.Left,
                position = point,
                delta = Vector2.zero
            };
        }

        private static bool TryGetScreenMetrics(RectTransform rectTransform, out Vector2 point, out Rect rect, out string error)
        {
            point = Vector2.zero;
            rect = new Rect();
            error = null;
            if (rectTransform == null)
            {
                error = "Missing RectTransform.";
                return false;
            }

            var canvas = rectTransform.GetComponentInParent<Canvas>();
            var camera = GetCanvasCamera(canvas);
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera == null)
            {
                error = "Unable to resolve camera for world-space UI.";
                return false;
            }

            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;
            for (var i = 0; i < corners.Length; i++)
            {
                var projected = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                minX = Mathf.Min(minX, projected.x);
                minY = Mathf.Min(minY, projected.y);
                maxX = Mathf.Max(maxX, projected.x);
                maxY = Mathf.Max(maxY, projected.y);
            }

            rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            point = rect.center;
            return true;
        }

        private static bool TryWorldToScreenPoint(Vector3 worldPosition, out Vector2 point, out string error)
        {
            point = Vector2.zero;
            error = null;
            var camera = Camera.main;
            if (camera == null)
            {
                error = "Camera.main is required to project non-UI targets.";
                return false;
            }

            var projected = camera.WorldToScreenPoint(worldPosition);
            if (projected.z < 0f)
            {
                error = "Target is behind Camera.main.";
                return false;
            }

            point = new Vector2(projected.x, projected.y);
            return true;
        }

        private static T[] FindSceneObjects<T>(bool includeInactive) where T : UnityEngine.Object
        {
            if (!includeInactive)
            {
                return UnityEngine.Object.FindObjectsOfType<T>();
            }

            var all = Resources.FindObjectsOfTypeAll<T>();
            var result = new List<T>();
            for (var i = 0; i < all.Length; i++)
            {
                if (IsSceneObject(all[i]))
                {
                    result.Add(all[i]);
                }
            }

            return result.ToArray();
        }

        private static bool IsSceneObject(UnityEngine.Object item)
        {
            if (item == null)
            {
                return false;
            }

            var component = item as Component;
            var go = component != null ? component.gameObject : item as GameObject;
            return go != null && go.scene.IsValid() && go.scene.isLoaded;
        }

        private static GameObject ResolveTarget(string path, int instanceId)
        {
            if (!string.IsNullOrEmpty(path))
            {
                var found = GameObject.Find(path);
                return found != null ? found : FindGameObjectByPath(path);
            }

            return instanceId == 0 ? null : FindGameObjectByInstanceId(instanceId);
        }

        private static GameObject FindGameObjectByPath(string path)
        {
            var objects = FindSceneObjects<GameObject>(true);
            for (var i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null && string.Equals(BuildTransformPath(objects[i].transform), path, StringComparison.Ordinal))
                {
                    return objects[i];
                }
            }

            return null;
        }

        private static GameObject FindGameObjectByInstanceId(int instanceId)
        {
            var objects = FindSceneObjects<GameObject>(true);
            for (var i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null && objects[i].GetInstanceID() == instanceId)
                {
                    return objects[i];
                }
            }

            return null;
        }

        private static bool MatchesKeyword(ButtonInfo entry, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }

            return ContainsText(entry.path, keyword)
                || ContainsText(entry.label, keyword)
                || ContainsText(entry.name, keyword)
                || ContainsText(entry.canvasPath, keyword)
                || ContainsText(entry.canvasName, keyword);
        }

        private static bool ContainsText(string value, string keyword)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(keyword)
                && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractButtonLabel(Button button)
        {
            var texts = button.GetComponentsInChildren<Text>(true);
            for (var i = 0; texts != null && i < texts.Length; i++)
            {
                if (texts[i] != null && !string.IsNullOrWhiteSpace(texts[i].text))
                {
                    return texts[i].text.Trim();
                }
            }

            var components = button.GetComponentsInChildren<Component>(true);
            for (var i = 0; components != null && i < components.Length; i++)
            {
                var value = TryReadTmpText(components[i]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return button.name;
        }

        private static string TryReadTmpText(Component component)
        {
            var type = component == null ? null : component.GetType();
            while (type != null)
            {
                if (type.FullName == "TMPro.TMP_Text" || type.Name.Contains("TMP_Text"))
                {
                    var property = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    return property == null ? null : property.GetValue(component, null) as string;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return null;
            }

            var path = transform.name;
            var parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static Camera GetCanvasCamera(Canvas canvas)
        {
            if (canvas == null)
            {
                return Camera.main;
            }

            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null;
            }

            return canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
        }

        private static GameObject GetSelectedGameObject()
        {
            return EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        }

        private static GameObject GetFirstRaycastTarget(List<RaycastResult> hits)
        {
            if (hits == null)
            {
                return null;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].gameObject != null)
                {
                    return hits[i].gameObject;
                }
            }

            return null;
        }

        private static int IndexOfGameObjectInRaycast(GameObject target, List<RaycastResult> hits)
        {
            if (target == null || hits == null)
            {
                return -1;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].gameObject == target)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CountClickableButtons(List<ButtonInfo> buttons)
        {
            var count = 0;
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].clickable)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool TrimHits(List<RaycastResult> hits, int maxResults)
        {
            if (hits.Count <= maxResults)
            {
                return false;
            }

            hits.RemoveRange(maxResults, hits.Count - maxResults);
            return true;
        }

        private static int ResolveMaxResults(int requested, int fallback)
        {
            return Math.Min(MaxResults, Math.Max(1, requested <= 0 ? fallback : requested));
        }

        private static int CompareButtonInfos(ButtonInfo left, ButtonInfo right)
        {
            var clickableCompare = right.clickable.CompareTo(left.clickable);
            if (clickableCompare != 0)
            {
                return clickableCompare;
            }

            var orderCompare = right.canvasSortingOrder.CompareTo(left.canvasSortingOrder);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            if (left.screenPointAvailable && right.screenPointAvailable)
            {
                var yCompare = right.clickPoint.y.CompareTo(left.clickPoint.y);
                if (yCompare != 0)
                {
                    return yCompare;
                }

                var xCompare = left.clickPoint.x.CompareTo(right.clickPoint.x);
                if (xCompare != 0)
                {
                    return xCompare;
                }
            }

            return string.CompareOrdinal(left.path, right.path);
        }

        private static object BuildScreenInfo()
        {
            return new
            {
                width = Screen.width,
                height = Screen.height,
                dpi = Screen.dpi,
                orientation = Screen.orientation.ToString(),
                coordinateOrigin = "bottom-left",
                safeArea = BuildRectInfo(Screen.safeArea)
            };
        }

        private static object BuildEventSystemInfo()
        {
            var eventSystem = EventSystem.current;
            return new
            {
                present = eventSystem != null,
                selected = BuildGameObjectInfo(GetSelectedGameObject()),
                currentInputModule = eventSystem != null && eventSystem.currentInputModule != null
                    ? eventSystem.currentInputModule.GetType().FullName
                    : null
            };
        }

        private static object[] BuildRaycastInfos(List<RaycastResult> hits)
        {
            var result = new object[hits == null ? 0 : hits.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = BuildRaycastInfo(hits[i], i);
            }

            return result;
        }

        private static object BuildRaycastInfo(RaycastResult hit, int index)
        {
            return new
            {
                index = index,
                gameObject = BuildGameObjectInfo(hit.gameObject),
                module = hit.module != null ? hit.module.GetType().FullName : null,
                moduleObject = hit.module != null ? BuildGameObjectInfo(hit.module.gameObject) : null,
                distance = hit.distance,
                depth = hit.depth,
                sortingLayer = hit.sortingLayer,
                sortingOrder = hit.sortingOrder,
                displayIndex = hit.displayIndex,
                screenPosition = BuildVector2Info(hit.screenPosition),
                worldPosition = BuildVector3Info(hit.worldPosition),
                worldNormal = BuildVector3Info(hit.worldNormal),
                isTopmost = index == 0
            };
        }

        private static object BuildGameObjectInfo(GameObject go)
        {
            return go == null ? null : new
            {
                name = go.name,
                path = BuildTransformPath(go.transform),
                instanceId = go.GetInstanceID(),
                activeInHierarchy = go.activeInHierarchy,
                layer = go.layer,
                tag = SafeTag(go)
            };
        }

        private static string SafeTag(GameObject go)
        {
            try { return go == null ? null : go.tag; }
            catch { return null; }
        }

        private static Vector2Info BuildVector2Info(Vector2 value)
        {
            return new Vector2Info { x = value.x, y = value.y };
        }

        private static object BuildVector3Info(Vector3 value)
        {
            return new { x = value.x, y = value.y, z = value.z };
        }

        private static object BuildRectInfo(Rect value)
        {
            return new
            {
                x = value.x,
                y = value.y,
                width = value.width,
                height = value.height,
                xMin = value.xMin,
                yMin = value.yMin,
                xMax = value.xMax,
                yMax = value.yMax,
                center = BuildVector2Info(value.center)
            };
        }

        public sealed class Vector2Info
        {
            public float x;
            public float y;
        }

        public sealed class ButtonInfo
        {
            public string name;
            public string label;
            public string path;
            public int instanceId;
            public bool activeInHierarchy;
            public bool enabled;
            public bool interactable;
            public bool clickable;
            public bool screenPointAvailable;
            public Vector2Info clickPoint;
            public bool screenRectAvailable;
            public object screenRect;
            public string screenMetricError;
            public bool raycastAvailable;
            public int raycastIndex;
            public int raycastCount;
            public string raycastError;
            public object raycastTarget;
            public bool topmost;
            public string canvasPath;
            public string canvasName;
            public int canvasSortingOrder;
            public string canvasRenderMode;
        }

        private sealed class ButtonCollection
        {
            public readonly List<ButtonInfo> entries = new List<ButtonInfo>();
            public readonly Dictionary<string, int> countsByCanvasPath = new Dictionary<string, int>();
            public int totalCount;
            public bool truncated;
        }

        private sealed class UiPointerState
        {
            public GameObject raycastTarget;
            public GameObject pointerPress;
            public GameObject clickHandler;
            public List<RaycastResult> raycastResults;
        }
    }
}
