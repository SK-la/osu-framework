// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Extensions;

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// Serves a single filesystem file for any requested resource name (used for outline fonts outside the game resources).
    /// </summary>
    public class FileResourceStore : IResourceStore<byte[]>
    {
        private readonly string path;

        public FileResourceStore(string path)
        {
            this.path = Path.GetFullPath(path);
        }

        public byte[] Get(string name)
        {
            using (Stream stream = GetStream(name))
                return stream?.ReadAllBytesToArray();
        }

        public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.Run(() => Get(name), cancellationToken);

        public Stream GetStream(string name)
        {
            if (!File.Exists(path))
                return null;

            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public IEnumerable<string> GetAvailableResources() => new[] { Path.GetFileName(path) };

        public void Dispose()
        {
        }
    }
}
