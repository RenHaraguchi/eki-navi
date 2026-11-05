using System;
using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 案内板1枚の登録内容。server/station_data/signs.json と GET /v1/station の signs と同じ形
    /// （contracts/signs.schema.json）。JsonUtility で読むので、フィールド名を変えないこと。
    /// </summary>
    [Serializable]
    public class SignEntry
    {
        public string id;
        public string name;
        public string imageName;
        public float x;
        public float z;
        public float ordinal;
        public float azimuthDeg;
        public float widthMeters;
        public float heightMeters;
        public float centerHeightMeters;
        public string mount;
        public float correctionYawDeg;
        public float correctionX;
        public float correctionZ;
        public string confidence;

        /// <summary>地図上の位置 (x, z)。補正は含まない。</summary>
        public Vector2 MapPosition => new Vector2(x, z);

        /// <summary>現地で決めた位置の補正 (x, z)。</summary>
        public Vector2 Correction => new Vector2(correctionX, correctionZ);

        /// <summary>この板を認識した結果から、地図から AR への変換を作る。補正も入れる。</summary>
        /// <param name="detectedPosition">AR で認識した板の位置 (x, z)。</param>
        /// <param name="detectedNormal">AR で認識した板の外向きの法線を水平にしたもの (x, z)。</param>
        public SignAlignment Align(Vector2 detectedPosition, Vector2 detectedNormal) =>
            SignAlignment.FromDetection(MapPosition, azimuthDeg, detectedPosition, detectedNormal, correctionYawDeg, Correction);
    }

    /// <summary>
    /// 案内板の一覧。アプリに同梱した signs.json（Assets/EkiNavi/Data/signs.json）か、
    /// GET /v1/station の応答から作る。参照画像ライブラリの名前（imageName）で引ける。
    /// </summary>
    public class SignCatalog
    {
        [Serializable]
        class SignFile
        {
            public SignEntry[] signs;
        }

        readonly List<SignEntry> signs;
        readonly Dictionary<string, SignEntry> byId = new Dictionary<string, SignEntry>();
        readonly Dictionary<string, SignEntry> byImageName = new Dictionary<string, SignEntry>();

        SignCatalog(List<SignEntry> signs)
        {
            this.signs = signs;
            foreach (var s in signs)
            {
                if (string.IsNullOrEmpty(s.id)) throw new FormatException("id が空の案内板があります");
                if (string.IsNullOrEmpty(s.imageName)) throw new FormatException($"{s.id}: imageName が空です");
                if (byId.ContainsKey(s.id)) throw new FormatException($"id が重複しています: {s.id}");
                if (byImageName.ContainsKey(s.imageName)) throw new FormatException($"imageName が重複しています: {s.imageName}");
                byId.Add(s.id, s);
                byImageName.Add(s.imageName, s);
            }
        }

        /// <summary>登録されている案内板。</summary>
        public IReadOnlyList<SignEntry> Signs => signs;

        /// <summary>
        /// {"signs": [...]} の形の JSON を読む。signs.json と GET /v1/station の応答のどちらでもよい。
        /// </summary>
        /// <exception cref="FormatException">案内板がない、または id か imageName が空・重複しているとき。</exception>
        public static SignCatalog Parse(string json)
        {
            SignFile file;
            try
            {
                file = JsonUtility.FromJson<SignFile>(json);
            }
            catch (Exception e)
            {
                throw new FormatException($"案内板の一覧を JSON として読めません: {e.Message}", e);
            }
            if (file?.signs == null || file.signs.Length == 0)
                throw new FormatException("案内板の一覧に signs がありません");
            return new SignCatalog(new List<SignEntry>(file.signs));
        }

        public bool TryGetById(string id, out SignEntry sign) => byId.TryGetValue(id ?? "", out sign);

        /// <summary>参照画像ライブラリに登録した名前（ARTrackedImage.referenceImage.name）から引く。</summary>
        public bool TryGetByImageName(string imageName, out SignEntry sign) =>
            byImageName.TryGetValue(imageName ?? "", out sign);
    }
}
