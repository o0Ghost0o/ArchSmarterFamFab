namespace ArchSmarterFamFab.Data
{
    /// <summary>A single source image (raw bytes + MIME type) fed to a model client.</summary>
    public class ImageInput
    {
        public byte[] Bytes { get; }
        public string MimeType { get; }

        public ImageInput(byte[] bytes, string mimeType)
        {
            Bytes = bytes;
            MimeType = mimeType;
        }
    }
}
