using UnityEngine;
using Arena.Logging;

namespace Arena.Player.States
{
    public class DeadPlayerState : IPlayerState
    {
        private readonly Player _player;
        private readonly IGameLogger _logger;
        
        public DeadPlayerState(Player player, IGameLogger logger)
        {
            _player = player;
            _logger = logger;
        }
        
        public void OnEnter()
        {
            _logger?.Log(LogLevel.Info, "Player", 
                "Player {0} entered DeadState", _player.PlayerId);
    
            if (_player.Rigidbody != null)
            {
                // init velocity before setting 'isKinematic'
                if (!_player.Rigidbody.isKinematic)
                {
                    _player.Rigidbody.linearVelocity = Vector3.zero;
                }
                _player.Rigidbody.isKinematic = true;
            }
        }
        
        public void OnExit()
        {
            if (_player.Rigidbody != null)
            {
                _player.Rigidbody.isKinematic = false;
            }
            
            _logger?.Log(LogLevel.Info, "Player", 
                "Player {0} exited DeadState", _player.PlayerId);
        }
        
        public void Update()
        {
            // Do Nothing
        }
        
        public void FixedUpdate()
        {
            // Do Nothing
        }
    }
}