using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>One batch of a page: drawn with one material at one sortingOrder.</summary>
    public sealed class BatchData
    {
        public int Order;
        public string Kind;          // "solid" | "textured"
        public bool Composite;
        public bool Atlas;           // texture atlas shared by several images and vector items
        public bool Opaque;          // opaque texture that may use DXT1 on PC (light mode)
        public string Texture;       // package relative path (textured only)
        public string ImageRef;
        public Vector3[] Vertices;
        public Color32[] Colors;
        public Vector2[] Uvs;
        public int[] Indices;
    }

    public sealed class PageData
    {
        public int Page;
        public Vector2 UnitySize;
        public Vector3 LayoutPosition;
        public List<BatchData> Batches = new List<BatchData>();
        public Dictionary<string, object> Json;
    }

    public sealed class TargetInfo
    {
        public string Name;
        public string Status;         // pass | needs-review | fail
        public string Profile;
        public string Compression;
        public int MaxTextureSize;
        public string ImageQuality;   // light | faithful
        public List<string> ReviewReasons = new List<string>();
        public List<string> BudgetFailures = new List<string>();
    }

    /// <summary>
    /// Reader/validator of an Asset Exchange Package written by pdf-unity-compiler
    /// (SPEC §7.2). All paths are validated against traversal and every file listed in
    /// the manifest is verified against its SHA-256 before use.
    /// </summary>
    public sealed class ExchangePackage
    {
        public const string Format = "pdf2unity-package";
        public const int SupportedSchema = 1;

        public string Root { get; }
        public Dictionary<string, object> Manifest { get; }
        public Dictionary<string, object> Diagnostics { get; }
        public string SourceName => J.Str(Manifest, "sourceName", "Document");
        public string SourceHash => J.Str(Manifest, "sourceHash", "");
        public float WorldScale => (float)J.Num(J.Obj(Manifest, "options"), "worldScale", 1.0);
        public readonly Dictionary<string, TargetInfo> Targets = new Dictionary<string, TargetInfo>();

        readonly Dictionary<string, string> assetHashes = new Dictionary<string, string>();

        public ExchangePackage(string root)
        {
            Root = Path.GetFullPath(root);
            Manifest = ReadJson("manifest.json") as Dictionary<string, object>
                       ?? throw new InvalidDataException("manifest.json is not an object");
            if (J.Str(Manifest, "format") != Format)
                throw new InvalidDataException("not a pdf2unity package");
            int schema = (int)J.Num(Manifest, "schemaVersion", -1);
            if (schema != SupportedSchema)
                throw new InvalidDataException($"unsupported package schema {schema} (expected {SupportedSchema})");
            var assets = J.Obj(Manifest, "assets");
            if (assets != null)
                foreach (var kv in assets) assetHashes[SafeRelative(kv.Key)] = kv.Value as string;
            Diagnostics = File.Exists(Path.Combine(Root, "diagnostics.json"))
                ? ReadJson("diagnostics.json") as Dictionary<string, object>
                : null;
            var targets = J.Obj(Manifest, "targets");
            if (targets != null)
            {
                foreach (var kv in targets)
                {
                    var t = new TargetInfo
                    {
                        Name = kv.Key,
                        Status = J.Str(kv.Value, "status", "fail"),
                        Profile = J.Str(kv.Value, "profile", ""),
                        Compression = J.Str(kv.Value, "textureCompression", "high"),
                        MaxTextureSize = (int)J.Num(kv.Value, "maxTextureSize", 4096),
                        ImageQuality = J.Str(kv.Value, "imageQuality", "faithful"),
                    };
                    foreach (var r in J.Arr(kv.Value, "reviewReasons") ?? new List<object>()) t.ReviewReasons.Add(r as string);
                    foreach (var r in J.Arr(kv.Value, "budgetFailures") ?? new List<object>()) t.BudgetFailures.Add(r as string);
                    Targets[kv.Key] = t;
                }
            }
        }

        /// <summary>Rejects absolute paths, drive letters and "..".</summary>
        public static string SafeRelative(string rel)
        {
            if (string.IsNullOrEmpty(rel)) throw new InvalidDataException("empty package path");
            var p = rel.Replace('\\', '/');
            if (p.StartsWith("/") || p.Contains(":") || Array.IndexOf(p.Split('/'), "..") >= 0)
                throw new InvalidDataException($"unsafe package path '{rel}'");
            return p;
        }

        public string FullPath(string rel)
        {
            var full = Path.GetFullPath(Path.Combine(Root, SafeRelative(rel)));
            if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"path escapes package: {rel}");
            return full;
        }

        public byte[] ReadVerified(string rel)
        {
            rel = SafeRelative(rel);
            var data = File.ReadAllBytes(FullPath(rel));
            if (assetHashes.TryGetValue(rel, out var expected) && expected != null)
            {
                var actual = "sha256:" + Sha256Hex(data);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"hash mismatch for {rel}");
            }
            else if (rel != "manifest.json" && rel != "diagnostics.json")
            {
                throw new InvalidDataException($"{rel} is not listed in the manifest");
            }
            return data;
        }

        object ReadJson(string rel)
        {
            var path = FullPath(rel);
            var data = rel == "manifest.json" || rel == "diagnostics.json" ? File.ReadAllBytes(path) : ReadVerified(rel);
            return Json.Parse(Encoding.UTF8.GetString(data));
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public List<int> PageNumbers()
        {
            var r = new List<int>();
            foreach (var p in J.Arr(Manifest, "pages")) r.Add((int)J.Num(p, "page"));
            return r;
        }

        public PageData LoadPage(int page, string target)
        {
            Dictionary<string, object> pinfo = null;
            foreach (var p in J.Arr(Manifest, "pages"))
                if ((int)J.Num(p, "page") == page) pinfo = p as Dictionary<string, object>;
            if (pinfo == null) throw new InvalidDataException($"page {page} not in manifest");
            var rel = J.Str(J.Obj(pinfo, "targets"), target)
                      ?? throw new InvalidDataException($"page {page} has no '{target}' output");
            var pj = ReadJson(rel) as Dictionary<string, object>;
            var data = new PageData { Page = page, Json = pj };
            var us = J.Floats(pj, "unitySize");
            data.UnitySize = new Vector2(us[0], us[1]);
            foreach (var l in J.Arr(Manifest, "layout") ?? new List<object>())
            {
                if ((int)J.Num(l, "page") != page) continue;
                var pos = J.Floats(l, "position");
                data.LayoutPosition = new Vector3(pos[0], pos[1], pos[2]);
            }
            var batches = J.Arr(pj, "batches");
            var geom = ReadVerified(J.Str(pj, "geometry"));
            ReadGeometry(geom, batches, data.Batches);
            return data;
        }

        /// <summary>Parses the P2UG binary geometry (see compiler package/writer.py).</summary>
        public static void ReadGeometry(byte[] geom, List<object> batchJson, List<BatchData> output)
        {
            using (var br = new BinaryReader(new MemoryStream(geom)))
            {
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "P2UG") throw new InvalidDataException("bad geometry magic");
                uint version = br.ReadUInt32();
                if (version != 1) throw new InvalidDataException($"unsupported geometry version {version}");
                int count = (int)br.ReadUInt32();
                if (batchJson != null && batchJson.Count != count) throw new InvalidDataException("batch count mismatch");
                for (int b = 0; b < count; b++)
                {
                    int nv = (int)br.ReadUInt32();
                    int ni = (int)br.ReadUInt32();
                    uint flags = br.ReadUInt32();
                    long remaining = br.BaseStream.Length - br.BaseStream.Position;
                    long need = (long)nv * (8 + 4 + ((flags & 1) != 0 ? 8 : 0)) + (long)ni * 4;
                    if (nv < 0 || ni < 0 || need > remaining || ni % 3 != 0) throw new InvalidDataException("corrupt geometry");
                    var bd = new BatchData { Vertices = new Vector3[nv], Colors = new Color32[nv], Indices = new int[ni] };
                    for (int k = 0; k < nv; k++) bd.Vertices[k] = new Vector3(br.ReadSingle(), br.ReadSingle(), 0f);
                    for (int k = 0; k < nv; k++)
                    {
                        bd.Colors[k] = new Color32(br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadByte());
                    }
                    if ((flags & 1) != 0)
                    {
                        bd.Uvs = new Vector2[nv];
                        for (int k = 0; k < nv; k++) bd.Uvs[k] = new Vector2(br.ReadSingle(), br.ReadSingle());
                    }
                    for (int k = 0; k < ni; k++)
                    {
                        uint ix = br.ReadUInt32();
                        if (ix >= nv) throw new InvalidDataException("index out of range");
                        bd.Indices[k] = (int)ix;
                    }
                    if (batchJson != null)
                    {
                        var j = batchJson[b];
                        bd.Order = (int)J.Num(j, "order", b);
                        bd.Kind = J.Str(j, "kind", "solid");
                        bd.Composite = J.Bool(j, "composite");
                        bd.Atlas = J.Bool(j, "atlas");
                        bd.Opaque = J.Bool(j, "opaque");
                        bd.Texture = J.Str(j, "texture");
                        bd.ImageRef = J.Str(j, "image");
                    }
                    else
                    {
                        bd.Order = b;
                        bd.Kind = bd.Uvs != null ? "textured" : "solid";
                    }
                    output.Add(bd);
                }
            }
        }
    }
}
