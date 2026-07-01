namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Thrown by any <see cref="IFamilyModelClient"/> implementation when a model API
    /// call fails or returns an unusable response. Carries the raw response JSON for logging.
    /// </summary>
    public class LlmException : Exception
    {
        public string ResponseJson { get; }

        public LlmException(string message, string responseJson)
            : base(message)
        {
            ResponseJson = responseJson;
        }
    }
}
