using CodexIsland;

namespace SideIsland.Core;

internal static class PortableBatchHash
{
    internal static BatchHashResult Run(IList<string> paths, string algorithm, string expectedManifest, CancellationToken cancellation,
        IProgress<BatchHashProgress> progress = null, Action<BatchHashItem> itemCompleted = null)
        => BatchHashTools.Run(paths, algorithm, expectedManifest, cancellation, progress, itemCompleted, new Provider());

    private sealed class Provider : IBatchHashProvider
    {
        public BatchHashSource Open(string path)
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new BatchHashReadException("not-regular", "Choose an ordinary file, not a directory or symbolic link.");
            return new Source(path);
        }
    }
    private sealed class Source : BatchHashSource
    {
        private readonly string path;
        private readonly FileStream stream;
        internal Source(string path) { this.path = path; stream = PortableBatchFiles.OpenRegularReadStream(path); }
        public override Stream Stream => stream;
        public override BatchHashStamp ReadStamp() => Stamp(stream);
        public override BatchHashStamp ReadPathStamp() { using var current = PortableBatchFiles.OpenRegularReadStream(path); return Stamp(current); }
        public override void Dispose() => stream.Dispose();
        private static BatchHashStamp Stamp(FileStream file)
        {
            var value = PortableBatchFiles.ReadOpenFileStamp(file.SafeFileHandle);
            return new(value.Identity, value.Length, value.ModifiedSeconds, value.ModifiedFraction);
        }
    }
}
