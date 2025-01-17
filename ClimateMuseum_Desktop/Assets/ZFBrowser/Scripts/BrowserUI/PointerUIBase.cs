﻿using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_2017_2_OR_NEWER
using UnityEngine.XR;
#endif

namespace ZenFulcrum.EmbeddedBrowser
{

    /// <summary>
    /// Base class that handles input for mouse/touch/pointer/VR/nose inputs.
    /// The concept is thus:
    ///   You have an arbitrary number of 3D (FPS player, VR pointer, and world ray) and
    ///   2D (mouse, touch, and screen position) pointers and you want any of them
    ///   to be able to interact with the Browser.
    /// 
    /// Concrete implementations of this class handle interacting with different rendered mediums
    /// (such as a mesh or a GUI renderer).
    /// </summary>
    [RequireComponent(typeof(Browser))]
    public abstract class PointerUIBase : MonoBehaviour, IBrowserUI
    {
        public readonly KeyEvents keyEvents = new KeyEvents();


        protected Browser browser;
        protected bool appFocused = true;

        /// <summary>
        /// Called once per tick. Handlers registered here should look at the pointers they have
        /// and tell us about them.
        /// </summary>
        public event Action onHandlePointers = () => { };

        protected int currentPointerId;

        protected readonly List<PointerState> currentPointers = new List<PointerState>();

        [Tooltip(
            "When clicking, how far (in browser-space pixels) must the cursor be moved before we send this movement to the browser backend? " +
            "This helps keep us from unintentionally dragging when we meant to just click, esp. under VR where it's hard to hold the cursor still."
        )]
        public float dragMovementThreshold = 0;
        /// <summary>
        /// Cursor location when we most recently went from 0 buttons to any buttons down.
        /// </summary>
        protected Vector2 mouseDownPosition;
        /// <summary>
        /// True when we have the any mouse button down AND we've moved at least dragMovementThreshold after that.
        /// </summary>
        protected bool dragging = false;

        #region Pointer Core

        public struct PointerState
        {
            /// <summary>
            /// Unique value for this pointer, to distinguish it by. Must be different for every
            /// pointer input.
            /// ids under 1000 are reserved for internal use.
            /// </summary>
            public int id;

            /// <summary>
            /// Is the pointer a 2d or 3d pointer?
            /// </summary>
            public bool is2D;

            /// <summary>
            /// Screen position of the pointer. Fill out only if is2D is true.
            /// </summary>
            public Vector2 position2D;

            /// <summary>
            /// World position of the pointer. Fill out only if is2D is false.
            /// </summary>
            public Ray position3D;

            /// <summary>
            /// Currently depressed "buttons" on this pointer.
            /// </summary>
            public MouseButton activeButtons;

            /// <summary>
            /// If the pointer can scroll, delta scrolling values since last frame.
            /// </summary>
            public Vector2 scrollDelta;
        }

        /// <summary>
        /// Called when the browser gets clicked with any mouse button.
        /// (More precisely, when we go from having no buttons down to 1+ buttons down.)
        /// </summary>
        public event Action onClick = () => { };


        public virtual void Awake()
        {
            BrowserCursor = new BrowserCursor();
            BrowserCursor.cursorChange += CursorUpdated;

            InputSettings = new BrowserInputSettings();

            browser = GetComponent<Browser>();
            browser.UIHandler = this;


            onHandlePointers += OnHandlePointers;

            if (disableMouseEmulation) Input.simulateMouseWithTouches = false;
        }

        public virtual void InputUpdate()
        {
            keyEvents.InputUpdate();

            p_currentDown = p_anyDown = p_currentOver = p_anyOver = -1;
            currentPointers.Clear();

            onHandlePointers();

            CalculatePointer();
        }

        public void OnApplicationFocus(bool focused)
        {
            appFocused = focused;
        }


        /// <summary>
        /// Converts a 2D screen-space coordinate to browser-space coordinates.
        /// If the given point doesn't map to the browser, return float.NaN for the position.
        /// </summary>
        /// <param name="screenPosition"></param>
        /// <param name="pointerId"></param>
        /// <returns></returns>
        protected abstract Vector2 MapPointerToBrowser(Vector2 screenPosition, int pointerId);

        /// <summary>
        /// Converts a 3D world-space ray to a browser-space coordinate.
        /// If the given ray doesn't map to the browser, return float.NaN for the position.
        /// </summary>
        /// <param name="worldRay"></param>
        /// <param name="pointerId"></param>
        /// <returns></returns>
        protected abstract Vector2 MapRayToBrowser(Ray worldRay, int pointerId);

        /// <summary>
        /// Returns the current position+rotation of the active pointer in world space.
        /// If there is none, or it doesn't make sense to map to world space, position will
        /// be NaNs.
        /// 
        /// Coordinates are in world space. The rotation should point up in the direction the browser sees as up.
        /// Z+ should go "into" the browser surface.
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="rot"></param>
        public abstract void GetCurrentHitLocation(out Vector3 pos, out Quaternion rot);

        /** Indexes into currentPointers for useful items this frame. -1 if N/A. */
        protected int p_currentDown, p_anyDown, p_currentOver, p_anyOver;

        /// <summary>
        /// Feeds the state of the given pointer into the handler.
        /// </summary>
        /// <param name="state"></param>
        public virtual void FeedPointerState(PointerState state)
        {
            if (state.is2D) state.position2D = MapPointerToBrowser(state.position2D, state.id);
            else
            {
                Debug.DrawRay(state.position3D.origin, state.position3D.direction * (Mathf.Min(500, maxDistance)), Color.cyan, 1.0f);
                state.position2D = MapRayToBrowser(state.position3D, state.id);
                //Debug.Log("Pointer " + state.id + " at " + state.position3D.origin + " pointing " + state.position3D.direction + " maps to  " + state.position2D + " for " + this.transform.root.name);
            }

            if (float.IsNaN(state.position2D.x)) return;

            if (state.id == currentPointerId)
            {
                p_currentOver = currentPointers.Count;

                if (state.activeButtons != 0) p_currentDown = currentPointers.Count;
            }
            else
            {
                p_anyOver = currentPointers.Count;

                if (state.activeButtons != 0) p_anyDown = currentPointers.Count;
            }

            currentPointers.Add(state);
        }

        protected virtual void CalculatePointer()
        {
            //		if (!appFocused) {
            //			MouseIsOff();
            //			return;
            //		}

            /*
             * The position/priority we feed to the browser is determined in this order:
             *   - Pointer we used earlier with a button down
             *   - Pointer with a button down
             *   - Pointer we used earlier 
             *   - Any pointer that is over the browser
             * Pointers that aren't over the browser are ignored.
             * If multiple pointers meet the criteria we may pick any that qualify.
             */

            PointerState stateToUse;
            if (p_currentDown >= 0)
            {
                //last frame's pointer with a button down
                stateToUse = currentPointers[p_currentDown];
            }
            else if (p_anyDown >= 0)
            {
                //mouse button count became > 0 this frame
                stateToUse = currentPointers[p_anyDown];
            }
            else if (p_currentOver >= 0)
            {
                //just hovering (use the pointer from last frame)
                stateToUse = currentPointers[p_currentOver];
            }
            else if (p_anyOver >= 0)
            {
                //just hovering (use any pointer over us)
                stateToUse = currentPointers[p_anyOver];
            }
            else
            {
                //no pointers over us
                MouseIsOff();
                return;
            }

            MouseIsOver();

            if (MouseButtons == 0 && stateToUse.activeButtons != 0)
            {
                //no buttons -> 1+ buttons
                onClick();
                //start drag prevention
                dragging = false;
                mouseDownPosition = stateToUse.position2D;
            }

            if (float.IsNaN(stateToUse.position2D.x)) Debug.LogError("Using an invalid pointer");// "shouldn't happen"

            if (stateToUse.activeButtons != 0 || MouseButtons != 0)
            {
                //Button(s) held or being released, do some extra logic to prevent unintentional dragging during clicks.

                if (!dragging && stateToUse.activeButtons != 0)
                {//only check distance if buttons(s) held and not already dragging
                 //Check to see if we passed the drag threshold.
                    var size = browser.Size;
                    var distance = Vector2.Distance(
                        Vector2.Scale(stateToUse.position2D, size),//convert from [0, 1] to pixels
                        Vector2.Scale(mouseDownPosition, size)//convert from [0, 1] to pixels
                    );

                    if (distance > dragMovementThreshold)
                    {
                        dragging = true;
                    }
                }

                if (dragging) MousePosition = stateToUse.position2D;
                else MousePosition = mouseDownPosition;
            }
            else
            {
                //no buttons held (or being release), no need to fiddle with the position
                MousePosition = stateToUse.position2D;
            }

            MouseButtons = stateToUse.activeButtons;
            MouseScroll = stateToUse.scrollDelta;
            currentPointerId = stateToUse.id;
        }


        public void OnGUI()
        {
            keyEvents.Feed(Event.current);
        }

        protected bool mouseWasOver = false;
        protected void MouseIsOver()
        {
            MouseHasFocus = true;
            KeyboardHasFocus = true;

            if (BrowserCursor != null)
            {
                CursorUpdated();
            }

            mouseWasOver = true;
        }

        protected void MouseIsOff()
        {
            //		if (BrowserCursor != null && mouseWasOver) {
            //			SetCursor(null);
            //		}
            mouseWasOver = false;

            MouseHasFocus = false;
            if (focusForceCount <= 0) KeyboardHasFocus = false;
            MouseButtons = 0;
            MouseScroll = Vector2.zero;

            currentPointerId = 0;
        }

        protected void CursorUpdated()
        {
            //		SetCursor(BrowserCursor);
        }

        protected int focusForceCount = 0;

        /// <summary>
        /// Sets a flag to keep the keyboard focus on this browser, even if it has no pointers.
        /// Useful for focusing it to type things in via external keyboard.
        /// 
        /// Call again with force = false to return to the default behavior. (You must call force
        /// on and force off an equal number of times to revert to the default behavior.)
        /// 
        /// </summary>
        /// <param name="force"></param>
        public void ForceKeyboardHasFocus(bool force)
        {
            if (force) ++focusForceCount;
            else --focusForceCount;
            if (focusForceCount == 1) KeyboardHasFocus = true;
            else if (focusForceCount == 0) KeyboardHasFocus = false;
        }

        #endregion

        #region Input Handlers

        [Tooltip("Camera to use to interpret 2D inputs and to originate FPS rays from. Defaults to Camera.main.")]
        public Camera viewCamera;

        public bool enableMouseInput = true;
        public bool enableTouchInput = true;
        public bool enableFPSInput = false;
        public bool enableVRInput = false;

        [Tooltip("(For ray-based interaction) How close must you be to the browser to be able to interact with it?")]
        public float maxDistance = float.PositiveInfinity;

        [Space(5)]
        [Tooltip("Disable Input.simulateMouseWithTouches globally. This will prevent touches from appearing as mouse events.")]
        public bool disableMouseEmulation = false;

        private MouseClickRobot currentProxy;
        public MouseClickRobot.PROXY_TYPE relevantProxyType { get; private set; } = MouseClickRobot.PROXY_TYPE.NONE;
        private int proxystate = 0;

        public void SetRelevantProxyType(MouseClickRobot.PROXY_TYPE type)
        {
            this.relevantProxyType = type;
        }

        protected virtual void OnHandlePointers()
        {
            if (enableFPSInput) FeedFPS();

            //special case to avoid duplicate pointer from the first touch (ignore mouse)
            if (enableMouseInput && enableTouchInput && Input.simulateMouseWithTouches && Input.touchCount > 0)
            {
                FeedTouchPointers();
                return;
            }

            if (enableMouseInput) FeedMousePointer();
            if (enableTouchInput) FeedTouchPointers();
#if UNITY_2017_2_OR_NEWER
            if (enableVRInput) FeedVRPointers();
#endif
            /*
            if (Input.GetKeyUp(KeyCode.B))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.INITIAL;
            }
            else if (Input.GetKeyUp(KeyCode.M))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.CENTER_IMAGE_EXPAND;
            }
            else if (Input.GetKeyUp(KeyCode.O))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.CENTER_IMAGE_EXTRACT;
            }
            else if (Input.GetKeyUp(KeyCode.P))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.Q))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.IMPACT_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.W))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.TEMP_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.E))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.CO2_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.R))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.GREENHOUSE_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.T))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.SEA_LEVEL_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.Z))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.OCEAN_ACIDIFICATION_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.U))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.AIR_POLLUTION_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.I))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.CROP_GRAPH;
            }
            else if (Input.GetKeyUp(KeyCode.A))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.COAL;
            }
            else if (Input.GetKeyUp(KeyCode.S))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.RENEWABLES;
            }
            else if (Input.GetKeyUp(KeyCode.D))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.OIL;
            }
            else if (Input.GetKeyUp(KeyCode.F))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.NUCLEAR;
            }
            else if (Input.GetKeyUp(KeyCode.G))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.NATURAL_GAS;
            }
            else if (Input.GetKeyUp(KeyCode.H))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.ZERO_CARBON;
            }
            else if (Input.GetKeyUp(KeyCode.J))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.BIOENERGY;
            }
            else if (Input.GetKeyUp(KeyCode.K))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.CARBON_PRICE;
            }
            else if (Input.GetKeyUp(KeyCode.L))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.TRANSPORT_ENERGY_EFFICIENCY;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha1))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.TRANSPORT_ELECTRIFICATION;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha2))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.BUILDINGS_ENERGY_EFFICIENCY;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha3))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.BUILDINGS_ELECTRIFICATION;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha4))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.GROWTH_POPULATION;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha5))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.GROWTH_ECONOMY;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha6))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.DEFORESTATION;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha7))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.METHANE;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha8))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.AFFORESTATION;
            }
            else if (Input.GetKeyUp(KeyCode.Alpha9))
            {
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.TECHNOLOGICAL;
            }
            */

            if (this.proxystate == 1)
            {
                Debug.Log("second click at " + this.transform.root.name);
                MouseClickRobot proxy = this.currentProxy;

                Ray ray = new Ray(proxy.transform.position, proxy.transform.forward);

                Debug.Log("emitting second ray for " + this.transform.root.name);

                var buttons = (MouseButton)0;

                PointerUIBase.PointerState state = new PointerUIBase.PointerState
                {
                    id = 100,
                    is2D = false,
                    position3D = ray,
                    activeButtons = buttons,
                    scrollDelta = Vector2.zero,
                };
                FeedPointerState(state);

                this.proxystate = 0;
                this.relevantProxyType = MouseClickRobot.PROXY_TYPE.NONE;
            }

            if (this.relevantProxyType != MouseClickRobot.PROXY_TYPE.NONE)
            {
                Debug.Log("first click at " + this.transform.root.name);
                MouseClickRobot[] proxies = this.gameObject.GetComponentsInChildren<MouseClickRobot>();

                MouseClickRobot relevantProxy = null;
                foreach (MouseClickRobot proxy in proxies)
                {
                    if (proxy.proxyType == this.relevantProxyType)
                    {
                        relevantProxy = proxy;
                        this.currentProxy = proxy;
                    }
                }

                if (relevantProxy)
                {
                    Ray ray = new Ray(relevantProxy.transform.position, relevantProxy.transform.forward);

                    Debug.Log("emitting ray for " + this.transform.root.name);

                    var buttons = (MouseButton)0;
                    buttons |= MouseButton.Left;

                    PointerUIBase.PointerState state = new PointerUIBase.PointerState
                    {
                        id = 100,
                        is2D = false,
                        position3D = ray,
                        activeButtons = buttons,
                        scrollDelta = Vector2.zero,
                    };
                    FeedPointerState(state);
                    this.proxystate = 1;
                }
                else
                {
                    this.relevantProxyType = MouseClickRobot.PROXY_TYPE.NONE;
                }
            }
        }

        /// <summary>
        /// Calls FeedPointerState with all the items in Input.touches.
        /// (Does not happen automatically, call when desired.)
        /// </summary>
        protected virtual void FeedTouchPointers()
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                var touch = Input.GetTouch(i);

                FeedPointerState(new PointerState
                {
                    id = 10 + touch.fingerId,
                    is2D = true,
                    position2D = touch.position,
                    activeButtons = (touch.phase == TouchPhase.Began || touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary) ? MouseButton.Left : 0,
                });
            }
        }

        /// <summary>
        /// Calls FeedPointerState with the current mouse state.
        /// (Does not happen automatically, call when desired.)
        /// </summary>
        protected virtual void FeedMousePointer()
        {
            var buttons = (MouseButton)0;
            if (Input.GetMouseButton(0)) buttons |= MouseButton.Left;
            if (Input.GetMouseButton(1)) buttons |= MouseButton.Right;
            if (Input.GetMouseButton(2)) buttons |= MouseButton.Middle;

            FeedPointerState(new PointerState
            {
                id = 1,
                is2D = true,
                position2D = Input.mousePosition,
                activeButtons = buttons,
                scrollDelta = Input.mouseScrollDelta,
            });
        }


        protected virtual void FeedFPS()
        {
            var buttons =
                (Input.GetButton("Fire1") ? MouseButton.Left : 0) |
                (Input.GetButton("Fire2") ? MouseButton.Right : 0) |
                (Input.GetButton("Fire3") ? MouseButton.Middle : 0)
            ;

            var camera = viewCamera ? viewCamera : Camera.main;

            var scrollDelta = Input.mouseScrollDelta;

            //Don't double-count scrolling if we are processing the mouse too.
            if (enableMouseInput) scrollDelta = Vector2.zero;

            FeedPointerState(new PointerState
            {
                id = 2,
                is2D = false,
                position3D = new Ray(camera.transform.position, camera.transform.forward),
                activeButtons = buttons,
                scrollDelta = scrollDelta,
            });
        }

#if UNITY_2017_2_OR_NEWER
        protected VRBrowserHand[] vrHands = null;
        protected virtual void FeedVRPointers()
        {
            if (vrHands == null)
            {
                vrHands = FindObjectsOfType<VRBrowserHand>();
                if (vrHands.Length == 0 && XRSettings.enabled)
                {
                    Debug.LogWarning("VR input is enabled, but no VRBrowserHands were found in the scene", this);
                }
            }

            for (int i = 0; i < vrHands.Length; i++)
            {
                if (!vrHands[i].Tracked) continue;

                FeedPointerState(new PointerState
                {
                    id = 100 + i,
                    is2D = false,
                    position3D = new Ray(vrHands[i].transform.position, vrHands[i].transform.forward),
                    activeButtons = vrHands[i].DepressedButtons,
                    scrollDelta = vrHands[i].ScrollDelta,
                });
            }

        }
#endif

        #endregion

        public virtual bool MouseHasFocus { get; protected set; }
        public virtual Vector2 MousePosition { get; protected set; }
        public virtual MouseButton MouseButtons { get; protected set; }
        public virtual Vector2 MouseScroll { get; protected set; }
        public virtual bool KeyboardHasFocus { get; protected set; }
        public virtual List<Event> KeyEvents { get { return keyEvents.Events; } }
        public virtual BrowserCursor BrowserCursor { get; protected set; }
        public virtual BrowserInputSettings InputSettings { get; protected set; }

    }

}
