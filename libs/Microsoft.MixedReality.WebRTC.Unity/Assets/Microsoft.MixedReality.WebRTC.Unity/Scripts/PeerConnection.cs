using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using System.Collections.Concurrent;

#if UNITY_WSA && !UNITY_EDITOR
using Windows.UI.Core;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Capture;
using Windows.ApplicationModel.Core;
#endif

namespace Microsoft.MixedReality.WebRTC.Unity
{
    /// <summary>
    /// Different Ice server types
    /// </summary>
    public enum IceType
    {
        /// <summary>
        /// Indicates there is no Ice information
        /// </summary>
        /// <remarks>
        /// Under normal use, this should not be used
        /// </remarks>
        None = 0,

        /// <summary>
        /// Indicates Ice information is of type STUN
        /// </summary>
        /// <remarks>
        /// https://en.wikipedia.org/wiki/STUN
        /// </remarks>
        Stun,

        /// <summary>
        /// Indicates Ice information is of type TURN
        /// </summary>
        /// <remarks>
        /// https://en.wikipedia.org/wiki/Traversal_Using_Relays_around_NAT
        /// </remarks>
        Turn
    }

    /// <summary>
    /// Represents an Ice server in a simple way that allows configuration from the unity inspector
    /// </summary>
    [Serializable]
    public struct ConfigurableIceServer
    {
        /// <summary>
        /// The type of the server
        /// </summary>
        [Tooltip("Type of ICE server")]
        public IceType Type;

        /// <summary>
        /// The unqualified uri of the server
        /// </summary>
        /// <remarks>
        /// You should not prefix this with "stun:" or "turn:"
        /// </remarks>
        [Tooltip("ICE server URI, without any stun: or turn: prefix.")]
        public string Uri;

        /// <summary>
        /// Convert the server to the representation the underlying libraries use
        /// </summary>
        /// <returns>stringified server information</returns>
        public override string ToString()
        {
            return string.Format("{0}: {1}", Type.ToString().ToLower(), Uri);
        }
    }

    /// <summary>
    /// A <see cref="UnityEvent"/> that represents a WebRTC error event.
    /// </summary>
    [Serializable]
    public class WebRTCErrorEvent : UnityEvent<string>
    {
    }

    /// <summary>
    /// High-level wrapper for Unity WebRTC functionalities.
    /// This is the API entry point for establishing a connection with a remote peer.
    /// </summary>
    public class PeerConnection : MonoBehaviour
    {
        /// <summary>
        /// Retrieves the underlying peer connection object.
        /// </summary>
        /// <remarks>
        /// If <see cref="OnInitialized"/> has not fired, this will be <c>null</c>.
        /// </remarks>
        public WebRTC.PeerConnection Peer
        {
            get
            {
                if (_nativePeer.Initialized)
                {
                    return _nativePeer;
                }
                return null;
            }
        }


        #region Signaling

        /// <summary>
        /// Signaler to use to establish the connection.
        /// </summary>
        [Tooltip("Signaler to use to establish the connection")]
        public Signaler Signaler;

        #endregion


        #region Behavior settings

        /// <summary>
        /// Flag to initialize the peer connection on <see cref="Start"/>.
        /// </summary>
        [Header("Behavior settings")]
        [Tooltip("Automatically initialize the peer connection on Start()")]
        public bool AutoInitializeOnStart = true;

        /// <summary>
        /// Flag to log all errors to the Unity console automatically.
        /// </summary>
        [Tooltip("Automatically log all errors to the Unity console")]
        public bool AutoLogErrorsToUnityConsole = true;

        #endregion


        #region Interactive Connectivity Establishment (ICE)

        /// <summary>
        /// Set of ICE servers the WebRTC library will use to try to establish a connection.
        /// </summary>
        [Header("ICE servers")]
        [Tooltip("Optional set of ICE servers (STUN and/or TURN)")]
        public List<ConfigurableIceServer> IceServers = new List<ConfigurableIceServer>()
        {
            new ConfigurableIceServer()
            {
                Type = IceType.Stun,
                Uri = "stun.l.google.com:19302"
            }
        };

        /// <summary>
        /// Optional username for the ICE servers.
        /// </summary>
        [Tooltip("Optional username for the ICE servers")]
        public string IceUsername;

        /// <summary>
        /// Optional credential for the ICE servers.
        /// </summary>
        [Tooltip("Optional credential for the ICE servers")]
        public string IceCredential;

        #endregion


        #region Events

        /// <summary>
        /// Event fired after the peer connection is initialized and ready for use.
        /// </summary>
        [Header("Peer connection events")]
        [Tooltip("Event fired after the peer connection is initialized and ready for use")]
        public UnityEvent OnInitialized = new UnityEvent();

        /// <summary>
        /// Event fired after the peer connection is shut down and cannot be used anymore.
        /// </summary>
        [Tooltip("Event fired after the peer connection is shut down and cannot be used anymore")]
        public UnityEvent OnShutdown = new UnityEvent();

        /// <summary>
        /// Event that occurs when a WebRTC error occurs
        /// </summary>
        [Tooltip("Event that occurs when a WebRTC error occurs")]
        public WebRTCErrorEvent OnError = new WebRTCErrorEvent();

        #endregion


        #region Private variables

        /// <summary>
        /// Internal queue used to marshal work back to the main unity thread
        /// </summary>
        private ConcurrentQueue<Action> _mainThreadWorkQueue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Underlying native peer connection wrapper.
        /// </summary>
        /// <remarks>
        /// Unlike the public <see cref="Peer"/> property, this is never <c>NULL</c>,
        /// but can be an uninitialized peer.
        /// </remarks>
        private WebRTC.PeerConnection _nativePeer = new WebRTC.PeerConnection();

        #endregion


        #region Public methods

        /// <summary>
        /// Enumerate the video capture devices available as a WebRTC local video feed source.
        /// </summary>
        /// <returns>The list of local video capture devices available to WebRTC.</returns>
        public static Task<List<WebRTC.PeerConnection.VideoCaptureDevice>> GetVideoCaptureDevicesAsync()
        {
            return WebRTC.PeerConnection.GetVideoCaptureDevicesAsync();
        }

        /// <summary>
        /// Initialize the underlying WebRTC libraries
        /// </summary>
        /// <remarks>
        /// This function is asynchronous, to monitor it's status bind a handler to OnInitialized and OnError
        /// </remarks>
        public Task InitializeAsync(CancellationToken token = default(CancellationToken))
        {
            // if the peer is already set, we refuse to initialize again.
            // Note: for multi-peer scenarios, use multiple WebRTC components.
            if (_nativePeer.Initialized)
            {
                return Task.CompletedTask;
            }

#if UNITY_ANDROID
            AndroidJavaClass systemClass = new AndroidJavaClass("java.lang.System");
            string libname = "jingle_peerconnection_so";
            systemClass.CallStatic("loadLibrary", new object[1] { libname });
            Debug.Log("loadLibrary loaded : " + libname);

            /*
                * Below is equivalent of this java code:
                * PeerConnectionFactory.InitializationOptions.Builder builder = 
                *   PeerConnectionFactory.InitializationOptions.builder(UnityPlayer.currentActivity);
                * PeerConnectionFactory.InitializationOptions options = 
                *   builder.createInitializationOptions();
                * PeerConnectionFactory.initialize(options);
                */

            AndroidJavaClass playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            AndroidJavaObject activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity");
            AndroidJavaClass webrtcClass = new AndroidJavaClass("org.webrtc.PeerConnectionFactory");
            AndroidJavaClass initOptionsClass = new AndroidJavaClass("org.webrtc.PeerConnectionFactory$InitializationOptions");
            AndroidJavaObject builder = initOptionsClass.CallStatic<AndroidJavaObject>("builder", new object[1] { activity });
            AndroidJavaObject options = builder.Call<AndroidJavaObject>("createInitializationOptions");

            if (webrtcClass != null)
            {
                webrtcClass.CallStatic("initialize", new object[1] { options });
            }
#endif

#if UNITY_WSA && !UNITY_EDITOR
            if (UnityEngine.WSA.Application.RunningOnUIThread())
#endif
            {
                return RequestAccessAndInitAsync(token);
            }
#if UNITY_WSA && !UNITY_EDITOR
            else
            {
                UnityEngine.WSA.Application.InvokeOnUIThread(() => RequestAccessAndInitAsync(token), waitUntilDone: true);
                return Task.CompletedTask;
            }
#endif
        }

        /// <summary>
        /// Uninitialize the underlying WebRTC library, effectively cleaning up the allocated peer connection.
        /// </summary>
        /// <remarks>
        /// <see cref="Peer"/> will be <c>null</c> afterward.
        /// </remarks>
        public void Uninitialize()
        {
            if (_nativePeer.Initialized)
            {
                Signaler.OnPeerUninitializing(this);

                // Close the connection and release native resources.
                _nativePeer.Dispose();
            }
            OnShutdown.Invoke();
        }

        #endregion


        #region Unity MonoBehaviour methods

        /// <summary>
        /// Unity Engine Start() hook
        /// </summary>
        /// <remarks>
        /// https://docs.unity3d.com/ScriptReference/MonoBehaviour.Start.html
        /// </remarks>
        private void Start()
        {
            if (AutoLogErrorsToUnityConsole)
            {
                OnError.AddListener(OnError_Listener);
            }

            GetVideoCaptureDevicesAsync().ContinueWith((prevTask) =>
            {
                var devices = prevTask.Result;
                _mainThreadWorkQueue.Enqueue(() =>
                {
                    foreach (var device in devices)
                    {
                        Debug.Log($"Video capture device {device.name} (id:{device.id}).");
                    }
                });
            });

            if (AutoInitializeOnStart)
            {
                InitializeAsync();
            }
        }

        /// <summary>
        /// Unity Engine Update() hook
        /// </summary>
        /// <remarks>
        /// https://docs.unity3d.com/ScriptReference/MonoBehaviour.Update.html
        /// </remarks>
        private void Update()
        {
            // Execute any pending work enqueued by background tasks
            while (_mainThreadWorkQueue.TryDequeue(out Action workload))
            {
                workload();
            }
        }

        /// <summary>
        /// Unity Engine OnDestroy() hook
        /// </summary>
        /// <remarks>
        /// https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnDestroy.html
        /// </remarks>
        private void OnDestroy()
        {
            Uninitialize();
            OnError.RemoveListener(OnError_Listener);
        }

        #endregion


        #region Private implementation

        /// <summary>
        /// Internal helper to ensure device access and continue initialization.
        /// </summary>
        /// <remarks>
        /// On UWP this must be called from the main UI thread.
        /// </remarks>
        private Task RequestAccessAndInitAsync(CancellationToken token)
        {
#if UNITY_WSA && !UNITY_EDITOR
            var mediaAccessRequester = new MediaCapture();
            var mediaSettings = new MediaCaptureInitializationSettings();
            mediaSettings.AudioDeviceId = "";
            mediaSettings.VideoDeviceId = "";
            mediaSettings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;
            mediaSettings.PhotoCaptureSource = PhotoCaptureSource.VideoPreview;
            var accessTask = mediaAccessRequester.InitializeAsync(mediaSettings).AsTask(token);
            return accessTask.ContinueWith(prevTask =>
            {
                token.ThrowIfCancellationRequested();

                if (prevTask.Exception == null)
                {
                    InitializePluginAsync(token);
                }
                else
                {
                    _mainThreadWorkQueue.Enqueue(() =>
                    {
                        OnError.Invoke($"Audio/Video access failure: {prevTask.Exception.Message}.");
                    });
                }
            }, token);
#else
            return InitializePluginAsync(token);
#endif
        }

        /// <summary>
        /// Internal handler to actually initialize the 
        /// </summary>
        private Task InitializePluginAsync(CancellationToken token)
        {
            Debug.Log("Initializing WebRTC plugin...");
            List<string> servers = IceServers.Select(i => i.ToString()).ToList();
            return _nativePeer.InitializeAsync(servers, IceUsername, IceCredential, token).ContinueWith((initTask) =>
            {
                token.ThrowIfCancellationRequested();

                if (initTask.Exception != null)
                {
                    _mainThreadWorkQueue.Enqueue(() =>
                    {
                        OnError.Invoke($"WebRTC plugin initializing failed : {initTask.Exception.Message}.");
                    });
                    throw initTask.Exception;
                }

                _mainThreadWorkQueue.Enqueue(() =>
                {
                    Debug.Log("WebRTC plugin initialized successfully.");
                    Signaler.OnPeerInitialized(this);
                    OnInitialized.Invoke();
                });
            }, token);
        }

        /// <summary>
        /// Internal handler for on-error, if <see cref="AutoLogErrorsToUnityConsole"/> is <c>true</c>
        /// </summary>
        /// <param name="error">The error message</param>
        private void OnError_Listener(string error)
        {
            Debug.LogError(error);
        }

        #endregion
    }
}