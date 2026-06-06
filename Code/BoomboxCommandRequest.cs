namespace Boombox
{
    public sealed class BoomboxCommandRequest
    {
        public BoomboxCommandType Type;
        public BoomboxCommandSource Source;
        public string Text = string.Empty;
        public int Number;
        public float Value;
        public Vector3i BlockPosition;
        public int ClrIdx;
    }
}
