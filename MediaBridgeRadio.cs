using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EasyDeliveryCoMediaBridge
{
    [BepInPlugin("alexunknown37.easydeliveryco.mediabridge", "MediaBridge Radio", "0.1.10")]
    public sealed class MediaBridgeRadio : BaseUnityPlugin
    {
        private const float SampleRate = 48000f;
        private const int Channels = 2;
        private const int AudioBufferSeconds = 3;
        private const int PrebufferSeconds = 1;
        private const int AudioBufferSize = (int)SampleRate * Channels * AudioBufferSeconds;
        private const int PrebufferSamples = (int)SampleRate * Channels * PrebufferSeconds;
        private static readonly FieldInfo LoaderInstance = AccessTools.Field(typeof(AudioLoader), "instance");
        private static readonly FieldInfo LoaderCustomChannel = AccessTools.Field(typeof(AudioLoader), "customChannel");
        private static readonly FieldInfo LoaderTracksLoaded = AccessTools.Field(typeof(AudioLoader), "tracksLoaded");
        private static readonly FieldInfo RadioScanning = AccessTools.Field(typeof(sRadioSystem), "scanning");
        private static readonly FieldInfo RadioScanningPaused = AccessTools.Field(typeof(sRadioSystem), "scanningPaused");
        private static readonly FieldInfo RadioVolume = AccessTools.Field(typeof(sRadioSystem), "radioVolume");
        private static readonly FieldInfo RadioNoise = AccessTools.Field(typeof(sRadioSystem), "noise");
        private static readonly FieldInfo PlayerRadioVolume = AccessTools.Field(typeof(sRadioPlayer), "radioVolume");
        private static readonly FieldInfo PlayerNoise = AccessTools.Field(typeof(sRadioPlayer), "noise");

        private static MediaBridgeRadio _instance;
        private readonly object _ringLock = new object();
        private readonly object _connectionLock = new object();
        private readonly object _sendLock = new object();
        private readonly Queue<string> _pendingMedia = new Queue<string>();
        private readonly float[] _ring = new float[AudioBufferSize];
        private readonly float[] _decode = new float[2048];
        private volatile bool _running;
        private volatile bool _connected;
        private int _ringRead, _ringWrite, _ringCount;
        private TcpClient _controlClient, _audioClient;
        private NetworkStream _controlStream;
        private Thread _controlThread, _audioThread;
        private AudioClip _streamClip;
        private RadioChannel _customChannel;
        private ConfigEntry<float> _stationFrequency;
        private ConfigEntry<float> _inputCooldown;
        private ConfigEntry<int> _controlPortOverride;
        private ConfigEntry<int> _audioPortOverride;
        private int _controlPort = 48900;
        private int _audioPort = 48901;
        private string _bridgeInstallPath;
        private float _nextChannelCheck;
        private float _lastTrackCommand = -10f;
        private int _lastHandledInputFrame = -1;
        private volatile bool _radioPlaybackOn = true;
        private bool _reportedNotRunning;
        private bool _triedLaunch;
        private bool _radioPlaybackKnown;
        private bool _reportedStationMismatch;
        private RadioChannel _reportedBoundChannel;
        private bool _reportedAudioData;
        private bool _reportedAudioCallback;
        private bool _reportedPrebufferWait;
        private bool _reportedPrebufferReady;
        private bool _reportedSignalOverride;
        private bool _reportedTruckSignalOverride;
        private string _nowPlaying = "MediaBridge";
        private float _nextPausedEnforcement;

        private void Awake()
        {
            _instance = this;
            _stationFrequency = Config.Bind("Radio", "CustomStationFrequency", 99.9f,
                "FM frequency of Easy Delivery Co.'s custom radio station.");
            _inputCooldown = Config.Bind("Radio", "TrackButtonCooldown", 0.75f,
                "Minimum seconds between next/previous commands from the radio controls.");
            _controlPortOverride = Config.Bind("MediaBridge", "ControlPort", 0,
                "Control port override. Leave at 0 to use MediaBridge's bridge.json or the default port.");
            _audioPortOverride = Config.Bind("MediaBridge", "AudioPort", 0,
                "Audio port override. Leave at 0 to use MediaBridge's bridge.json or the default port.");
            ReadBridgeInfo();
            if (_controlPortOverride.Value > 0) _controlPort = _controlPortOverride.Value;
            if (_audioPortOverride.Value > 0) _audioPort = _audioPortOverride.Value;

            _streamClip = AudioClip.Create("MediaBridge", 48000 * 60 * 60, 2, 48000, true, ReadPcmData);
            _running = true;
            _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "MediaBridge control" };
            _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "MediaBridge audio" };
            _controlThread.Start();
            _audioThread.Start();

            var harmony = new Harmony("alexunknown37.easydeliveryco.mediabridge");
            var callbackInput = AccessTools.Method(typeof(sRadioSystem), "SetInput", new[] { typeof(InputAction.CallbackContext) });
            var vectorInput = AccessTools.Method(typeof(sRadioSystem), "SetInput", new[] { typeof(Vector2) });
            var changeChannel = AccessTools.Method(typeof(sRadioSystem), "ChangeChannel", new[] { typeof(int), typeof(bool), typeof(bool) });
            if (callbackInput == null && vectorInput == null)
                Logger.LogWarning("Radio input hooks were not found; music playback and now-playing text can still work.");
            else
            {
                if (callbackInput != null)
                    harmony.Patch(callbackInput, prefix: new HarmonyMethod(typeof(MediaBridgeRadio), nameof(SetInputCallbackPrefix)));
                if (vectorInput != null)
                    harmony.Patch(vectorInput, prefix: new HarmonyMethod(typeof(MediaBridgeRadio), nameof(SetInputPrefix)));
                if (changeChannel != null)
                    harmony.Patch(changeChannel, prefix: new HarmonyMethod(typeof(MediaBridgeRadio), nameof(ChangeChannelPrefix)));
                Logger.LogInfo("Radio input and channel-change hooks installed.");
            }

            var doSignal = AccessTools.Method(typeof(sRadioSystem), "DoSignal");
            if (doSignal != null)
                harmony.Patch(doSignal, postfix: new HarmonyMethod(typeof(MediaBridgeRadio), nameof(DoSignalPostfix)));
            else
                Logger.LogWarning("Radio signal hook was not found; MediaBridge playback may still fade with the game's signal strength.");

            var playerDoSignal = AccessTools.Method(typeof(sRadioPlayer), "DoSignal");
            if (playerDoSignal != null)
                harmony.Patch(playerDoSignal, postfix: new HarmonyMethod(typeof(MediaBridgeRadio), nameof(PlayerDoSignalPostfix)));
            else
                Logger.LogWarning("Truck radio signal hook was not found; vehicle playback may still fade with the game's signal strength.");

            Logger.LogInfo("MediaBridge Radio loaded. Tune to the game's custom station (FM 99.9 by default).");
        }

        private void Update()
        {
            lock (_pendingMedia)
            {
                while (_pendingMedia.Count > 0)
                {
                    ControlMessage message;
                    try { message = JsonUtility.FromJson<ControlMessage>(_pendingMedia.Dequeue()); }
                    catch { continue; }
                    if (message == null) continue;
                    var state = new MediaState { title = message.title, artist = message.artist };
                    var trackName = BuildTrackName(state.title, state.artist);
                    if (!string.Equals(trackName, _nowPlaying, StringComparison.Ordinal))
                    {
                        _nowPlaying = trackName;
                        if (_streamClip != null) _streamClip.name = _nowPlaying;
                        ClearAudioBuffer();
                    }
                }
            }

            if (Time.unscaledTime >= _nextChannelCheck)
            {
                _nextChannelCheck = Time.unscaledTime + 1f;
                StopScanningOnCustomStation(sRadioSystem.instance);
                AttachStreamToCustomStation();
            }

            // The game's radio update can restart its source while MediaBridge is paused.
            // Stop it again so a pause cannot leave a second burst of buffered audio playing.
            if (!_radioPlaybackOn && Time.unscaledTime >= _nextPausedEnforcement)
            {
                _nextPausedEnforcement = Time.unscaledTime + 0.1f;
                StopMediaBridgeSources();
            }

        }

        private void OnDestroy()
        {
            _running = false;
            lock (_connectionLock)
            {
                try { if (_controlClient != null) _controlClient.Close(); } catch { }
                try { if (_audioClient != null) _audioClient.Close(); } catch { }
            }
            if (_streamClip != null) Destroy(_streamClip);
            if (_instance == this) _instance = null;
        }

        private void AttachStreamToCustomStation()
        {
            if (_streamClip == null || LoaderInstance == null || LoaderCustomChannel == null) return;

            var loader = LoaderInstance.GetValue(null) as AudioLoader;
            if (loader == null) return;
            var loaderChannel = LoaderCustomChannel.GetValue(loader) as RadioChannel;
            if (loaderChannel == null) return;

            var loaded = LoaderTracksLoaded == null || (bool)LoaderTracksLoaded.GetValue(loader);
            if (!loaded) return; // AudioLoader fills externalTracks after loading local custom-radio files.

            var radio = sRadioSystem.instance;
            if (radio == null || radio.channels == null || radio.currentChannelIndex < 0 || radio.currentChannelIndex >= radio.channels.Count) return;
            if (radio.source == null) return;

            var selectedChannel = radio.channels[radio.currentChannelIndex];
            if (selectedChannel == null) return;

            // The game's custom-channel loader can report a placeholder frequency (for example
            // 88.1) while the playable custom station exposed by the radio is 99.9. The
            // selected channel is the actual one whose AudioSource and HUD are in use, so bind
            // the MediaBridge clip there when it matches the configured station frequency.
            var selected = ReferenceEquals(selectedChannel, loaderChannel)
                || Math.Abs(selectedChannel.frequency - loaderChannel.frequency) < 0.02f
                || Math.Abs(selectedChannel.frequency - 99.9f) < 0.02f
                || Math.Abs(selectedChannel.frequency - _stationFrequency.Value) < 0.02f;
            if (!selected)
            {
                if (!_reportedStationMismatch)
                {
                    _reportedStationMismatch = true;
                    Logger.LogInfo("Custom audio channel FM " + loaderChannel.frequency + "; current station is FM " +
                        (selectedChannel == null ? "unknown" : selectedChannel.frequency.ToString("0.0")) + ".");
                }
                return;
            }

            int bufferedSamples;
            lock (_ringLock) bufferedSamples = _ringCount;
            if (bufferedSamples < PrebufferSamples)
            {
                if (!_reportedPrebufferWait)
                {
                    _reportedPrebufferWait = true;
                    Logger.LogInfo("Waiting for MediaBridge audio prebuffer (" + bufferedSamples + "/" + PrebufferSamples + " samples).");
                }
                return;
            }
            if (!_reportedPrebufferReady)
            {
                _reportedPrebufferReady = true;
                Logger.LogInfo("MediaBridge audio prebuffer ready; starting radio playback.");
            }
            var tracks = selectedChannel.externalTracks;
            if (tracks == null)
            {
                tracks = new List<AudioClip>();
                selectedChannel.externalTracks = tracks;
            }
            if (!tracks.Contains(_streamClip)) tracks.Add(_streamClip);
            selectedChannel.queue = new[] { _streamClip };
            _customChannel = selectedChannel;

            if (radio.source.clip != _streamClip)
            {
                radio.source.Stop();
                radio.source.resource = _streamClip;
                radio.source.clip = _streamClip;
                radio.source.time = 0f;
                if (!_radioPlaybackKnown)
                {
                    _radioPlaybackOn = radio.source.enabled;
                    _radioPlaybackKnown = true;
                }
                if (_radioPlaybackOn)
                {
                    radio.source.enabled = true;
                    radio.source.Play();
                }
                else radio.source.Pause();
                Logger.LogInfo("MediaBridge stream attached to selected radio station FM " + selectedChannel.frequency +
                    " (loader channel FM " + loaderChannel.frequency + ").");
            }
            if (!ReferenceEquals(_reportedBoundChannel, selectedChannel))
            {
                _reportedBoundChannel = selectedChannel;
                Logger.LogInfo("MediaBridge radio binding confirmed for selected station FM " + selectedChannel.frequency + ".");
            }
        }

        private bool IsCustomStation(sRadioSystem radio)
        {
            if (radio == null || radio.channels == null || radio.currentChannelIndex < 0 || radio.currentChannelIndex >= radio.channels.Count)
                return false;

            var channel = radio.channels[radio.currentChannelIndex];
            return IsCustomChannel(channel);
        }

        private bool IsCustomChannel(RadioChannel channel)
        {
            if (channel == null) return false;
            if (_customChannel != null && ReferenceEquals(channel, _customChannel)) return true;
            var loader = LoaderInstance == null ? null : LoaderInstance.GetValue(null) as AudioLoader;
            var loaderChannel = loader == null || LoaderCustomChannel == null
                ? null
                : LoaderCustomChannel.GetValue(loader) as RadioChannel;
            return ReferenceEquals(channel, loaderChannel)
                || Math.Abs(channel.frequency - 99.9f) < 0.02f
                || Math.Abs(channel.frequency - _stationFrequency.Value) < 0.02f;
        }

        private static bool SetInputCallbackPrefix(sRadioSystem __instance, InputAction.CallbackContext context)
        {
            if (!context.performed) return true;
            return HandleInputValue(__instance, context.ReadValue<Vector2>());
        }

        private static bool SetInputPrefix(sRadioSystem __instance, Vector2 __0)
        {
            return HandleInputValue(__instance, __0);
        }

        private static bool ChangeChannelPrefix(sRadioSystem __instance, int __0)
        {
            var plugin = _instance;
            if (plugin == null || __0 == 0 || !plugin.IsCustomStation(__instance)) return true;

            // The game also changes stations directly during scan. Keep the MediaBridge station
            // selected and translate any remaining left/right channel step into a track skip.
            plugin.HandleRadioInput(__0 > 0 ? "next" : "previous");
            return false;
        }

        private static void DoSignalPostfix(sRadioSystem __instance)
        {
            var plugin = _instance;
            if (plugin == null || !plugin.IsCustomStation(__instance)) return;

            // Easy Delivery Co fades music using the selected station's tower signal. The
            // custom station has no matching tower, so that fade can mute MediaBridge even
            // though its audio stream remains healthy. Keep streamed music at radioVolume.
            var volume = RadioVolume == null ? 1f : (float)RadioVolume.GetValue(__instance);
            if (__instance.source != null) __instance.source.volume = volume;
            var noise = RadioNoise == null ? null : RadioNoise.GetValue(__instance) as AudioSource;
            if (noise != null) noise.volume = 0f;
            if (!plugin._reportedSignalOverride)
            {
                plugin._reportedSignalOverride = true;
                plugin.Logger.LogInfo("Disabled game signal fading for the selected MediaBridge station.");
            }
        }

        private static void PlayerDoSignalPostfix(sRadioPlayer __instance)
        {
            var plugin = _instance;
            if (plugin == null || __instance == null || !plugin.IsCustomTruckStation(__instance)) return;

            var volume = PlayerRadioVolume == null ? 1f : (float)PlayerRadioVolume.GetValue(__instance);
            if (__instance.source != null) __instance.source.volume = volume;
            var noise = PlayerNoise == null ? null : PlayerNoise.GetValue(__instance) as AudioSource;
            if (noise != null) noise.volume = 0f;
            if (!plugin._reportedTruckSignalOverride)
            {
                plugin._reportedTruckSignalOverride = true;
                plugin.Logger.LogInfo("Disabled game signal fading on the truck radio player.");
            }
        }

        private bool IsCustomTruckStation(sRadioPlayer player)
        {
            if (player == null) return false;
            if (Math.Abs(player.frequency - 99.9f) < 0.02f || Math.Abs(player.frequency - _stationFrequency.Value) < 0.02f)
                return true;

            var radio = sRadioSystem.instance;
            return radio != null && radio.channels != null && player.currentChannelIndex >= 0
                && player.currentChannelIndex < radio.channels.Count
                && IsCustomChannel(radio.channels[player.currentChannelIndex]);
        }

        private static bool HandleInputValue(sRadioSystem radio, Vector2 input)
        {
            var plugin = _instance;
            if (plugin == null || !plugin.IsCustomStation(radio)) return true;

            if (input.y < -0.5f)
            {
                plugin.ToggleRadioPlayback();
                return false;
            }

            if (input.y > 0.5f)
            {
                plugin.StopScanningOnCustomStation(radio);
                return false; // Up has no MediaBridge action; do not let scan tune away from it.
            }

            if (Math.Abs(input.x) >= 0.5f)
            {
                plugin.HandleRadioInput(input.x > 0f ? "next" : "previous");
                return false;
            }

            return true; // up keeps the game's normal scan behavior.
        }

        private void HandleRadioInput(string command)
        {
            if (_lastHandledInputFrame == Time.frameCount) return;
            _lastHandledInputFrame = Time.frameCount;
            if (Time.unscaledTime - _lastTrackCommand < _inputCooldown.Value) return;
            _lastTrackCommand = Time.unscaledTime;
            SendCommand(command);
        }

        private void ToggleRadioPlayback()
        {
            if (_lastHandledInputFrame == Time.frameCount) return;
            _lastHandledInputFrame = Time.frameCount;
            var radio = sRadioSystem.instance;
            if (!_radioPlaybackKnown && radio != null && radio.source != null)
            {
                _radioPlaybackOn = radio.source.isPlaying;
                _radioPlaybackKnown = true;
            }
            _radioPlaybackOn = !_radioPlaybackOn;
            ClearAudioBuffer();
            SendCommand(_radioPlaybackOn ? "play" : "pause");
            if (!_radioPlaybackOn) StopMediaBridgeSources();

            if (radio != null && radio.source != null && radio.source.clip == _streamClip)
            {
                if (_radioPlaybackOn)
                {
                    radio.source.enabled = true;
                    radio.source.Play();
                }
            }
        }

        private void StopMediaBridgeSources()
        {
            var radio = sRadioSystem.instance;
            if (radio != null && radio.source != null && radio.source.clip == _streamClip)
                radio.source.Stop();

            // The truck's spatial radio player can own a separate AudioSource from the
            // station selector. Stop those too, or it can continue the last DSP-buffered
            // audio after the selector source has stopped.
            var players = FindObjectsByType<sRadioPlayer>(FindObjectsSortMode.None);
            for (var i = 0; i < players.Length; i++)
            {
                var player = players[i];
                if (IsCustomTruckStation(player) && player.source != null && player.source.isPlaying)
                    player.source.Stop();
            }
        }

        private void StopScanningOnCustomStation(sRadioSystem radio)
        {
            if (!IsCustomStation(radio)) return;
            if (RadioScanning != null) RadioScanning.SetValue(radio, false);
            if (RadioScanningPaused != null) RadioScanningPaused.SetValue(radio, false);
        }

        private void SendCommand(string command)
        {
            if (!_connected)
            {
                Logger.LogWarning("Radio command '" + command + "' was ignored because MediaBridge is not connected.");
                return;
            }
            try
            {
                lock (_sendLock)
                {
                    if (_controlStream == null)
                    {
                        Logger.LogWarning("Radio command '" + command + "' was ignored because the MediaBridge control connection is unavailable.");
                        return;
                    }
                    var bytes = Encoding.UTF8.GetBytes("{\"cmd\":\"" + command + "\"}\n");
                    _controlStream.Write(bytes, 0, bytes.Length);
                    Logger.LogInfo("Sent MediaBridge radio command: " + command + ".");
                }
            }
            catch (Exception e) { Logger.LogDebug("MediaBridge command could not be sent: " + e.Message); }
        }

        private void ReadBridgeInfo()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var paths = new[]
            {
                Path.Combine(local, "MediaBridge", "bridge.json"),
                @"Z:\tmp\MediaBridge\bridge.json"
            };

            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var info = JsonUtility.FromJson<BridgeInfo>(File.ReadAllText(path));
                    if (info == null) continue;
                    if (info.controlPort > 0) _controlPort = info.controlPort;
                    if (info.audioPort > 0) _audioPort = info.audioPort;
                    _bridgeInstallPath = info.installPath;
                    Logger.LogInfo("Using MediaBridge ports " + _controlPort + " and " + _audioPort + ".");
                    return;
                }
                catch (Exception e) { Logger.LogDebug("Could not read MediaBridge discovery info: " + e.Message); }
            }
        }

        private void TryStartInstalledMediaBridge()
        {
            if (_triedLaunch) return;
            _triedLaunch = true;
            if (string.IsNullOrEmpty(_bridgeInstallPath) || !Path.GetExtension(_bridgeInstallPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                if (!File.Exists(_bridgeInstallPath)) return;
                Process.Start(new ProcessStartInfo { FileName = _bridgeInstallPath, UseShellExecute = true });
                Logger.LogInfo("Started the installed MediaBridge app.");
            }
            catch (Exception e) { Logger.LogWarning("Could not start MediaBridge: " + e.Message); }
        }

        private void ControlLoop()
        {
            while (_running)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect("127.0.0.1", _controlPort);
                        lock (_connectionLock) _controlClient = client;
                        var stream = client.GetStream();
                        lock (_sendLock)
                        {
                            _controlStream = stream;
                            var hello = Encoding.UTF8.GetBytes("{\"cmd\":\"hello\",\"name\":\"Easy Delivery Co MediaBridge Radio\"}\n");
                            stream.Write(hello, 0, hello.Length);
                        }
                        _connected = true;
                        Logger.LogInfo("Connected to MediaBridge control.");
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string line;
                            while (_running && (line = reader.ReadLine()) != null)
                            {
                                var message = JsonUtility.FromJson<ControlMessage>(line);
                                if (message != null && message.type == "event" && message.@event == "media")
                                {
                                    lock (_pendingMedia) _pendingMedia.Enqueue(line);
                                }
                            }
                        }
                    }
                }
                catch (SocketException)
                {
                    if (!_reportedNotRunning)
                    {
                        _reportedNotRunning = true;
                        TryStartInstalledMediaBridge();
                        Logger.LogInfo("MediaBridge is not running yet. Start it if it is not installed; the mod will keep reconnecting.");
                    }
                }
                catch (Exception e) { if (_running) Logger.LogDebug("MediaBridge control disconnected: " + e.Message); }
                finally
                {
                    _connected = false;
                    lock (_sendLock) _controlStream = null;
                    lock (_connectionLock) _controlClient = null;
                }
                if (_running) Thread.Sleep(2000);
            }
        }

        private void AudioLoop()
        {
            while (_running)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect("127.0.0.1", _audioPort);
                        lock (_connectionLock) _audioClient = client;
                        var stream = client.GetStream();
                        var header = new byte[16];
                        if (!ReadExact(stream, header, header.Length) || header[0] != (byte)'M' || header[1] != (byte)'B' || header[2] != (byte)'A' || header[3] != (byte)'1')
                            throw new IOException("Invalid MediaBridge audio header.");

                        var rate = BitConverter.ToInt32(header, 4);
                        var channels = BitConverter.ToInt16(header, 8);
                        var format = BitConverter.ToInt16(header, 10);
                        if (rate != 48000 || channels != 2 || format != 3)
                            throw new IOException("Unsupported MediaBridge audio format.");

                        Logger.LogInfo("Connected to MediaBridge audio stream (48 kHz stereo float PCM).");

                        var bytes = new byte[8192 + 8];
                        int carry = 0;
                        while (_running)
                        {
                            var read = stream.Read(bytes, carry, 8192);
                            if (read == 0) break;
                            var total = carry + read;
                            var usable = total - total % 8; // preserve left/right sample alignment
                            var count = usable / 4;
                            if (count > _decode.Length) count = _decode.Length;
                            for (var i = 0; i < count; i++) _decode[i] = BitConverter.ToSingle(bytes, i * 4);
                            PushAudio(_decode, count);
                            if (count > 0 && !_reportedAudioData)
                            {
                                _reportedAudioData = true;
                                Logger.LogInfo("Receiving MediaBridge audio samples.");
                            }
                            carry = total - usable;
                            if (carry > 0) Buffer.BlockCopy(bytes, usable, bytes, 0, carry);
                        }
                    }
                }
                catch (SocketException) { }
                catch (Exception e) { if (_running) Logger.LogDebug("MediaBridge audio disconnected: " + e.Message); }
                finally { lock (_connectionLock) _audioClient = null; }
                if (_running) Thread.Sleep(1000);
            }
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        private void PushAudio(float[] samples, int count)
        {
            lock (_ringLock)
            {
                if (!_radioPlaybackOn)
                {
                    _ringRead = _ringWrite = _ringCount = 0;
                    return;
                }
                for (var i = 0; i < count; i++)
                {
                    if (_ringCount == _ring.Length)
                    {
                        _ringRead = (_ringRead + 1) % _ring.Length;
                        _ringCount--;
                    }
                    _ring[_ringWrite] = samples[i];
                    _ringWrite = (_ringWrite + 1) % _ring.Length;
                    _ringCount++;
                }
            }
        }

        private void ReadPcmData(float[] data)
        {
            Array.Clear(data, 0, data.Length);
            if (!_reportedAudioCallback)
            {
                _reportedAudioCallback = true;
                Logger.LogInfo("Unity radio audio source is consuming MediaBridge stream data.");
            }
            lock (_ringLock)
            {
                if (!_radioPlaybackOn)
                {
                    _ringRead = _ringWrite = _ringCount = 0;
                    return;
                }
                var count = Math.Min(data.Length, _ringCount);
                for (var i = 0; i < count; i++)
                {
                    data[i] = _ring[_ringRead];
                    _ringRead = (_ringRead + 1) % _ring.Length;
                }
                _ringCount -= count;
            }
        }

        private void ClearAudioBuffer()
        {
            lock (_ringLock)
            {
                _ringRead = _ringWrite = _ringCount = 0;
            }
        }

        private static string BuildTrackName(string title, string artist)
        {
            title = (title ?? "").Trim();
            artist = (artist ?? "").Trim();
            if (title.Length == 0 && artist.Length == 0) return "MediaBridge";
            if (title.Length == 0) return artist;
            if (artist.Length == 0) return title;
            return title + " - " + artist;
        }

        [Serializable]
        private sealed class ControlMessage
        {
            public string type;
            public string @event;
            public string title;
            public string artist;
        }

        [Serializable]
        private sealed class BridgeInfo
        {
            public int controlPort;
            public int audioPort;
            public string installPath;
        }

        private sealed class MediaState
        {
            public string title;
            public string artist;
        }
    }
}
