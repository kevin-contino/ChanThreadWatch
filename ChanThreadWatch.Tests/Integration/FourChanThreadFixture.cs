using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace JDP.Tests.Integration {
    // The 4chan-thread-loopback.html template plus deterministic bytes for its four images and three
    // thumbnails. Image and thumbnail URLs point at whatever base URLs the test passes in.
    public sealed class FourChanThreadFixture {
        public const string ThreadPath = "/wg/thread/100";
        public const string ThreadName = "100";

        // Server paths of the images, in post order. Image 4 has no data-md5 in the markup.
        public static readonly string[] ImagePaths = {
            "/wg/1700000000001.jpg", "/wg/1700000000002.png", "/wg/1700000000003.png", "/wg/1700000000004.gif"
        };

        // Server paths of the thumbnails; both spoilered posts share the one spoiler placeholder
        public static readonly string[] ThumbPaths = {
            "/wg/1700000000001s.jpg", "/wg/1700000000002s.jpg", "/image/spoiler-wg.png"
        };

        public Dictionary<string, byte[]> Images { get; } = new Dictionary<string, byte[]>();
        public Dictionary<string, byte[]> Thumbs { get; } = new Dictionary<string, byte[]>();

        public FourChanThreadFixture() {
            for (int i = 0; i < ImagePaths.Length; i++) {
                // One image is larger than the 8 KB read buffer so it arrives in several chunks
                Images[ImagePaths[i]] = MakeBytes(i + 1, i == 0 ? 20000 : 1000 + i * 300);
            }
            for (int i = 0; i < ThumbPaths.Length; i++) {
                Thumbs[ThumbPaths[i]] = MakeBytes(100 + i, 200 + i * 50);
            }
        }

        public static string FileName(string path) {
            return path.Substring(path.LastIndexOf('/') + 1);
        }

        public string Html(string imageBaseURL, string thumbBaseURL) {
            string html = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "4chan-thread-loopback.html"));
            html = html.Replace("{{img}}", imageBaseURL).Replace("{{thumb}}", thumbBaseURL);
            for (int i = 0; i < 3; i++) {
                html = html.Replace("{{md5_" + (i + 1) + "}}", MD5Base64(Images[ImagePaths[i]]));
            }
            return html;
        }

        public void RouteThread(LoopbackHttpServer server, string imageBaseURL, string thumbBaseURL) {
            server.Route(ThreadPath, LoopbackResponse.Html(Html(imageBaseURL, thumbBaseURL)));
        }

        public void RouteImages(LoopbackHttpServer server) {
            foreach (KeyValuePair<string, byte[]> image in Images) {
                server.Route(image.Key, LoopbackResponse.Bytes(image.Value, "image/png"));
            }
        }

        public void RouteThumbs(LoopbackHttpServer server) {
            foreach (KeyValuePair<string, byte[]> thumb in Thumbs) {
                server.Route(thumb.Key, LoopbackResponse.Bytes(thumb.Value, "image/jpeg"));
            }
        }

        // Thread page, images and thumbnails all on one server
        public void RouteAll(LoopbackHttpServer server) {
            RouteThread(server, server.BaseURL(), server.BaseURL());
            RouteImages(server);
            RouteThumbs(server);
        }

        public static byte[] MakeBytes(int seed, int length) {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static string MD5Base64(byte[] data) {
            using (MD5 md5 = MD5.Create()) {
                return Convert.ToBase64String(md5.ComputeHash(data));
            }
        }
    }
}
