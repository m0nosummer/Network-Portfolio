#if ARENA_AOS_FOG_OF_WAR
using FischlWorks_FogWar;
#endif
using UnityEngine;

namespace Arena.Vision
{
    internal sealed class OptionalFogOfWarBridge
    {
#if ARENA_AOS_FOG_OF_WAR
        private readonly csFogWar _fogWar;

        private OptionalFogOfWarBridge(csFogWar fogWar)
        {
            _fogWar = fogWar;
        }
#endif

        public bool IsAvailable
        {
            get
            {
#if ARENA_AOS_FOG_OF_WAR
                return _fogWar != null;
#else
                return false;
#endif
            }
        }

        public static bool TryCreate(out OptionalFogOfWarBridge bridge)
        {
#if ARENA_AOS_FOG_OF_WAR
            var fogWar = Object.FindAnyObjectByType<csFogWar>();
            if (fogWar != null)
            {
                bridge = new OptionalFogOfWarBridge(fogWar);
                return true;
            }
#endif

            bridge = null;
            return false;
        }

        public bool TryCheckVisibility(Vector3 worldPosition, int teamIndex, out bool isVisible)
        {
#if ARENA_AOS_FOG_OF_WAR
            if (_fogWar != null)
            {
                isVisible = _fogWar.CheckVisibility(worldPosition, teamIndex);
                return true;
            }
#endif

            isVisible = true;
            return false;
        }

        public bool TrySetFogPlaneAlpha(float alpha)
        {
#if ARENA_AOS_FOG_OF_WAR
            if (_fogWar != null)
            {
                _fogWar.FogPlaneAlpha = alpha;
                return true;
            }
#endif

            return false;
        }

        public bool TryAddRevealer(
            Transform target,
            int circleRange,
            int coneRange,
            float coneAngle,
            out int revealerIndex)
        {
#if ARENA_AOS_FOG_OF_WAR
            if (_fogWar != null && target != null)
            {
                var revealer = new csFogWar.FogRevealer(
                    target,
                    circleRange,
                    coneRange,
                    coneAngle,
                    false);

                revealerIndex = _fogWar.AddFogRevealer(revealer);
                return revealerIndex >= 0;
            }
#endif

            revealerIndex = -1;
            return false;
        }

        public bool TryRemoveRevealer(int revealerIndex)
        {
#if ARENA_AOS_FOG_OF_WAR
            if (_fogWar != null && revealerIndex >= 0)
            {
                _fogWar.RemoveFogRevealer(revealerIndex);
                return true;
            }
#endif

            return false;
        }

        public bool TrySetConeDirection(int revealerIndex, Vector3 direction)
        {
#if ARENA_AOS_FOG_OF_WAR
            if (_fogWar == null || revealerIndex < 0 || direction.sqrMagnitude <= 0f)
                return false;

            if (revealerIndex >= _fogWar._FogRevealers.Count)
                return false;

            _fogWar._FogRevealers[revealerIndex].SetConeDirection(direction.normalized);
            return true;
#else
            return false;
#endif
        }
    }
}
