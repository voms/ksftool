using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace KsfCompanion.Ui
{
    /// <summary>
    /// Downloads map previews (from ksf.surf) and avatars once, shrinks them and keeps them in
    /// ~/.cache/ksf-companion/images so the dashboard stays quick.
    /// </summary>
    sealed class ImageCache
    {
        const int StoredWidth = 1600;
        readonly HttpClient http;
        readonly string dir;
        // Thumbnails (the nominate page can ask for dozens) and the pictures of the map you're on don't share a
        // queue: the big picture never waits behind thumbnails.
        readonly SemaphoreSlim downloads = new SemaphoreSlim(4, 4);
        readonly SemaphoreSlim urgentDownloads = new SemaphoreSlim(2, 2);

        public ImageCache(HttpClient http)
        {
            this.http = http;
            dir = Path.Combine(Program.CacheDir, "images");
            Directory.CreateDirectory(dir);
        }

        public Task<Bitmap> MapAsync(string map, int width, bool urgent = false) => LoadAsync("map_" + map.ToLowerInvariant(), KsfApi.MapImage(map), width, urgent);

        public Task<Bitmap> AvatarAsync(string url) => LoadAsync("avatar_" + Hash(url), url, 96);

        /// <summary>The map picture shrunk to a few dozen pixels and blurred: a soft wash of its colours for behind the dashboard.</summary>
        public async Task<Bitmap> AmbientAsync(string map)
        {
            // Downloaded (or already there) like the other sizes; the blur is made from the file.
            if (await MapAsync(map, 40, urgent: true).ConfigureAwait(false) == null) return null;
            return await Task.Run(() => Blur(Path.Combine(dir, "map_" + map.ToLowerInvariant() + ".jpg"))).ConfigureAwait(false);
        }

        static Bitmap Blur(string file)
        {
            try
            {
                using var original = SKBitmap.Decode(file);
                if (original == null) return null;
                var height = Math.Max(1, (int)Math.Round(original.Height * 40.0 / original.Width));
                using var small = original.Resize(new SKImageInfo(40, height, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
                if (small == null) return null;
                int width = small.Width;
                var pixels = small.Bytes;
                var temp = new byte[pixels.Length];
                // Three box blurs in a row come out close to a gaussian.
                for (var pass = 0; pass < 3; pass++)
                {
                    BoxBlur(pixels, temp, width, small.Height, 3, 1, 0);
                    BoxBlur(temp, pixels, width, small.Height, 3, 0, 1);
                }
                using var blurred = new SKBitmap(small.Info);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, blurred.GetPixels(), pixels.Length);
                using var png = blurred.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = new MemoryStream(png.ToArray());
                return new Bitmap(stream);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        static void BoxBlur(byte[] source, byte[] target, int width, int height, int radius, int dx, int dy)
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            for (var c = 0; c < 4; c++)
            {
                int sum = 0, count = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    int sx = x + k * dx, sy = y + k * dy;
                    if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                    sum += source[(sy * width + sx) * 4 + c];
                    count++;
                }
                target[(y * width + x) * 4 + c] = (byte)(sum / count);
            }
        }

        async Task<Bitmap> LoadAsync(string key, string url, int width, bool urgent = false)
        {
            var file = Path.Combine(dir, key + ".jpg");
            var missing = file + ".missing";
            if (!File.Exists(file))
            {
                // Don't ask again for a day when a map has no preview.
                if (File.Exists(missing) && File.GetLastWriteTimeUtc(missing) > DateTime.UtcNow.AddDays(-1)) return null;

                var lane = urgent ? urgentDownloads : downloads;
                await lane.WaitAsync().ConfigureAwait(false);
                try
                {
                    // A slow or busy moment on ksf.surf: try again before giving up on the picture.
                    for (var attempt = 1; !File.Exists(file); attempt++)
                    {
                        try
                        {
                            using var response = await http.GetAsync(url).ConfigureAwait(false);
                            // Only "there's no such picture" counts as missing - not a busy or failing server.
                            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                File.WriteAllText(missing, "");
                                return null;
                            }
                            response.EnsureSuccessStatusCode();
                            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            await Task.Run(() => Store(bytes, file)).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (attempt < 3 && (ex is HttpRequestException || ex is TaskCanceledException))
                        {
                            await Task.Delay(1500 * attempt).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is NotSupportedException)
                {
                    return null;
                }
                finally
                {
                    lane.Release();
                }
            }

            return await Task.Run(() => Decode(file, width)).ConfigureAwait(false);
        }

        static void Store(byte[] bytes, string file)
        {
            using var frame = SKBitmap.Decode(bytes) ?? throw new NotSupportedException("not a picture");
            using var image = frame.Width > StoredWidth
                ? frame.Resize(new SKImageInfo(StoredWidth, Math.Max(1, (int)Math.Round(frame.Height * (double)StoredWidth / frame.Width))), SKFilterQuality.High)
                : frame.Copy();
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 88) ?? throw new NotSupportedException("couldn't save the picture");
            var temp = file + ".tmp";
            using (var output = File.Create(temp)) jpeg.SaveTo(output);
            File.Move(temp, file, overwrite: true);
        }

        static Bitmap Decode(string file, int width)
        {
            try
            {
                using var stream = File.OpenRead(file);
                return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is IOException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            {
                try { File.Delete(file); } catch (IOException) { }
                return null;
            }
        }

        static string Hash(string text)
        {
            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
        }
    }
}
