namespace ArchSmarterFamFab.Data
{
    /// <summary>
    /// Thrown by any <see cref="IFamilyModelClient"/> implementation when a model API
    /// call fails or returns an unusable response. Carries the raw response JSON for logging.
    /// </summary>
    public class LlmException : Exception
    {
        public string ResponseJson { get; }

        /// <summary>A short, display-safe excerpt of the raw response body for error dialogs.</summary>
        public string Detail =>
            string.IsNullOrEmpty(ResponseJson)
                ? ""
                : "\n\n" + (ResponseJson.Length > 500 ? ResponseJson.Substring(0, 500) + "…" : ResponseJson);

        public LlmException(string message, string responseJson)
            : base(message)
        {
            ResponseJson = responseJson;
        }
    }
}
