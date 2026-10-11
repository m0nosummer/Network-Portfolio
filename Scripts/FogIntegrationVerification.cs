#if ARENA_AOS_FOG_OF_WAR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Arena.Core;
using Arena.Core.DependencyInjection;
using Arena.Input;
using Arena.Network;
using Arena.Network.Messages;
using Arena.Vision;
using FischlWorks_FogWar;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using ArenaPlayer = Arena.Player.Player;

namespace Arena.Verification
{
    public sealed class FogIntegrationVerification : MonoBehaviour
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private csFogWar _fog;
        private ArenaPlayer _player;
        private NetworkConfig _config;
        private GameTimerUI _timer;
        private VisionReceiver _vision;
        private Mouse _mouse;
        private VerificationLog _log;
        private int _checks;
        private Vector3 _origin;
        private Vector3 _far;
        private GameObject _remote;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Environment.GetCommandLineArgs().Contains("-arena-fog-check"))
                new GameObject(nameof(FogIntegrationVerification)).AddComponent<FogIntegrationVerification>();
        }

        private IEnumerator Start()
        {
            _log = new VerificationLog(true);
            yield return null;
            yield return null;
            if (!Step(Prepare)) yield break;
            yield return null;
            yield return null;
            if (!Step(VerifyDay)) yield break;
            yield return new WaitForSecondsRealtime(0.5f);
            if (!Step(() => Capture("fog-day.png"))) yield break;
            yield return new WaitForSecondsRealtime(0.3f);
            if (!Step(VerifyNight)) yield break;
            yield return new WaitForSecondsRealtime(0.5f);
            if (!Step(() => Capture("fog-night.png"))) yield break;
            yield return new WaitForSecondsRealtime(0.3f);
            if (!Step(VerifyRestoreAndCleanup)) yield break;
            yield return null;
            if (!Step(() =>
            {
                Check(_fog._FogRevealers.Count == 0, "destroy removes local revealer");
                _log.ThrowIfErrors();
            })) yield break;
            Debug.Log($"FOG|PASS|checks={_checks}|unity={Application.unityVersion}|graphics={SystemInfo.graphicsDeviceType}");
            Application.Quit(0);
        }

        private bool Step(Action action)
        {
            try { action(); _log.ThrowIfErrors(); return true; }
            catch (Exception ex)
            {
                Debug.LogError($"FOG|FAIL|{ex}");
                Application.Quit(1);
                return false;
            }
        }

        private void Prepare()
        {
            Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "graphics device active");
            _fog = FindAnyObjectByType<csFogWar>();
            _timer = FindAnyObjectByType<GameTimerUI>();
            _vision = FindAnyObjectByType<VisionReceiver>();
            _config = GameInstaller.Container.Resolve<NetworkConfig>();
            Check(_fog != null && _timer != null && _vision != null, "scene components present");
            Check(_fog.levelData.levelDimensionX == 55 && _fog.levelData.levelDimensionY == 55, "original 55x55 map scanned");
            var plane = GameObject.Find("[RUNTIME] Fog_Plane");
            var material = plane.GetComponent<Renderer>().sharedMaterial;
            Check(material.shader.name == "FogWar/FogPlane" && material.shader.isSupported, "fog shader supported");
            Check(material.mainTexture != null, "fog texture bound");

            FindClearCorridor();
            Set(_vision, "_localPlayerId", 9001);
            Set(_vision, "_isInitialized", true);
            var manager = FindAnyObjectByType<NetworkTestManager>();
            var prefab = (GameObject)typeof(NetworkTestManager).GetField("playerPrefab", Private).GetValue(manager);
            _player = Instantiate(prefab, _origin, Quaternion.identity).GetComponent<ArenaPlayer>();
            _player.SetLocalPlayer(true);
            _player.SetPlayerId(9001);
            _mouse = InputSystem.AddDevice<Mouse>("FogVerificationMouse");
            _mouse.MakeCurrent();

            _remote = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _remote.name = "FogVerificationRemote";
            _remote.transform.position = _far;
            _remote.GetComponent<Collider>().enabled = false;
            _remote.GetComponent<Renderer>().sharedMaterial = prefab.GetComponentInChildren<Renderer>().sharedMaterial;
            _vision.RegisterPlayer(9002, _remote);

            var follow = Camera.main.GetComponent<Arena.Gameplay.CameraFollow>();
            if (follow != null) follow.enabled = false;
            Camera.main.transform.position = new Vector3(0, 42, 0);
            Camera.main.transform.rotation = Quaternion.Euler(90, 0, 0);
            Camera.main.orthographic = true;
            Camera.main.orthographicSize = 28;
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Exclude)) canvas.enabled = false;
        }

        private void FindClearCorridor()
        {
            int distance = Mathf.RoundToInt((_config.DayConeVisionRange + _config.NightConeVisionRange) * 0.5f);
            for (int z = 2; z < 53; z++)
            for (int x = 2; x + distance < 53; x++)
            {
                bool empty = true;
                for (int i = -2; i <= distance; i++)
                    if (_fog.levelData[x + i][z] != csFogWar.LevelColumn.ETileState.Empty) empty = false;
                if (!empty) continue;
                Vector3 start = new Vector3(_fog.GetWorldX(x), 1, _fog.GetWorldY(z));
                Vector3 end = start + Vector3.right * distance;
                if (Mathf.Abs(start.x) > 17 || Mathf.Abs(start.z) > 17 || Mathf.Abs(end.x) > 17) continue;
                _origin = start;
                _far = end;
                Debug.Log($"FOG|CORRIDOR|origin={_origin}|target={_far}|distance={distance}");
                return;
            }
            throw new InvalidOperationException("No clear day/night comparison corridor in scene");
        }

        private void VerifyDay()
        {
            _player.enabled = false;
            _player.GetComponent<PlayerInputHandler>().enabled = false;
            _player.Rigidbody.isKinematic = true;
            _player.transform.position = _origin;
            Camera.main.transform.SetPositionAndRotation(new Vector3(0, 42, 0), Quaternion.Euler(90, 0, 0));
            Check(_player.GetComponentsInChildren<Renderer>().All(r => r.enabled), "local player stays visible");
            SetTime(false);
            Check(_fog._FogRevealers.Count == 1, "one local revealer registered");
            var revealer = _fog._FogRevealers.Single();
            Check(revealer._UseConeVision && revealer._SightRange == (int)_config.DayCircleVisionRange &&
                  revealer._ConeSightRange == (int)_config.DayConeVisionRange &&
                  Mathf.Approximately(revealer._ConeAngle, _config.DayVisionAngle), "day parameters applied");
            Aim(Vector3.right);
            Check(Vector3.Dot(revealer.GetConeDirection(), Vector3.right) > 0.99f, "mouse aim reaches cone direction");
            Check(_fog.CheckVisibility(_far, 0), "day forward target visible");
            Check(_remote.GetComponent<Renderer>().enabled, "remote renderer visible inside cone");
            Check(_fog.CheckVisibility(_origin - Vector3.right * 2, 0), "near circle reveals behind player");
            Aim(Vector3.left);
            Check(!_fog.CheckVisibility(_far, 0), "rotation hides distant target");
            Check(!_remote.GetComponent<Renderer>().enabled, "remote renderer hidden outside cone");
            Aim(Vector3.right);
            Check(Mathf.Approximately(_fog.FogPlaneAlpha, _config.DayFogOpacity), "day fog opacity applied");
            VerifyWallOcclusion();
            VerifyTexture();
        }

        private void VerifyWallOcclusion()
        {
            Vector3 saved = _player.transform.position;
            for (int z = 2; z < 53; z++)
            for (int x = 2; x + 12 < 53; x++)
            {
                if (_fog.levelData[x][z] != csFogWar.LevelColumn.ETileState.Empty ||
                    _fog.levelData[x + 12][z] != csFogWar.LevelColumn.ETileState.Empty) continue;
                bool wall = false;
                for (int i = 1; i < 12; i++)
                    if (_fog.levelData[x + i][z] == csFogWar.LevelColumn.ETileState.Obstacle) wall = true;
                if (!wall) continue;
                _player.transform.position = new Vector3(_fog.GetWorldX(x), 1, _fog.GetWorldY(z));
                Aim(Vector3.right);
                Check(!_fog.CheckVisibility(_player.transform.position + Vector3.right * 12, 0), "scanned wall blocks target within range");
                _player.transform.position = saved;
                Aim(Vector3.right);
                return;
            }
            throw new InvalidOperationException("No wall occlusion sample found");
        }

        private void VerifyNight()
        {
            SetTime(true);
            Aim(Vector3.right);
            Check(_fog._FogRevealers.Count == 1, "night replaces rather than duplicates revealer");
            var revealer = _fog._FogRevealers.Single();
            Check(revealer._SightRange == (int)_config.NightCircleVisionRange &&
                  revealer._ConeSightRange == (int)_config.NightConeVisionRange &&
                  Mathf.Approximately(revealer._ConeAngle, _config.NightVisionAngle), "night parameters applied");
            Check(!_fog.CheckVisibility(_far, 0) && !_remote.GetComponent<Renderer>().enabled, "night range hides daytime target");
            Check(Mathf.Approximately(_fog.FogPlaneAlpha, _config.NightFogOpacity), "night fog opacity applied");
            VerifyTexture();
        }

        private void VerifyRestoreAndCleanup()
        {
            SetTime(false);
            Aim(Vector3.right);
            Check(_fog.CheckVisibility(_far, 0) && _remote.GetComponent<Renderer>().enabled, "day restoration shows target again");
            _vision.UnregisterPlayer(9002);
            Destroy(_remote);
            Destroy(_player.gameObject);
            InputSystem.RemoveDevice(_mouse);
        }

        private void SetTime(bool night) => Call(_timer, "OnGameTimeMessage",
            new GameTimeMessage { IsNight = night, RemainingTime = 10 }, 0);

        private void Aim(Vector3 direction)
        {
            Vector3 screen = Camera.main.WorldToScreenPoint(_player.transform.position + direction * 8);
            _mouse.MakeCurrent();
            InputSystem.QueueStateEvent(_mouse, new MouseState { position = new Vector2(screen.x, screen.y) });
            InputSystem.Update();
            Call(_player, "UpdateFogConeDirection");
            Call(_fog, "ForceUpdateFog");
            Call(_vision, "UpdatePlayerVisibility");
        }

        private void VerifyTexture()
        {
            var target = (Texture2D)typeof(csFogWar).GetField("fogPlaneTextureLerpTarget", Private).GetValue(_fog);
            Color[] pixels = target.GetPixels();
            Check(pixels.Any(p => p.a < 0.01f) && pixels.Any(p => p.a > 0.5f), "fog texture contains revealed and covered cells");
        }

        private void Capture(string filename)
        {
            string folder = Environment.GetEnvironmentVariable("ARENA_FOG_CAPTURE");
            if (string.IsNullOrWhiteSpace(folder)) return;
            Directory.CreateDirectory(folder);
            var target = new RenderTexture(1120, 900, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(1120, 900, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target };
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(Camera.main, request);
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1120, 900), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(Path.Combine(folder, filename), pixels.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                target.Release();
                Destroy(target);
                Destroy(pixels);
            }
        }

        private void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            _checks++;
            Debug.Log($"FOG|CHECK_PASS|{label}");
        }

        private static void Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private).Invoke(target, args);
        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);
    }
}
#endif
