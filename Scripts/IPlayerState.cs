namespace Arena.Player
{
    public interface IPlayerState
    {
        void OnEnter();
        void OnExit();
        void Update();
        void FixedUpdate();
    }
}