using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 案内板を認識したときの、地図から AR への変換（設計書5章、contracts/README.md の「位置合わせ」）。
    /// 地図の点 P の AR 座標 = 回転(YawDeg) × (P − 案内板の地図上の位置) + 認識した位置。
    /// 基準の実装は server/app/alignment.py で、答えは contracts/alignment_vectors.json にある。
    /// 水平の座標は Vector2 の (x, y) に (x, z) を入れて扱う。高さはこの変換では決めない。
    /// </summary>
    public readonly struct SignAlignment
    {
        /// <summary>地図を AR に重ねるための回転。-180 度より大きく 180 度以下。</summary>
        public readonly float YawDeg;

        /// <summary>案内板の地図上の位置 (x, z)。現地の補正を足したもの。</summary>
        public readonly Vector2 SignMap;

        /// <summary>案内板の AR 上の位置 (x, z)。</summary>
        public readonly Vector2 SignAr;

        public SignAlignment(float yawDeg, Vector2 signMap, Vector2 signAr)
        {
            YawDeg = yawDeg;
            SignMap = signMap;
            SignAr = signAr;
        }

        /// <summary>水平の回転。上方向は重力（Unity の y）のまま。</summary>
        public Quaternion Rotation => Quaternion.Euler(0f, YawDeg, 0f);

        /// <summary>
        /// 案内板の認識結果と一覧の値から変換を作る。
        /// </summary>
        /// <param name="signMap">一覧に登録した案内板の位置 (x, z)。signs.json の x, z。</param>
        /// <param name="signAzimuthDeg">一覧に登録した板面の外向きの方位。signs.json の azimuthDeg。</param>
        /// <param name="detectedPosition">AR で認識した板の位置 (x, z)。</param>
        /// <param name="detectedNormal">AR で認識した板の外向きの法線を水平にしたもの (x, z)。<see cref="SignDetection.TryHorizontalNormal"/> で作る。</param>
        /// <param name="correctionYawDeg">現地で決めた向きの補正。signs.json の correctionYawDeg。</param>
        /// <param name="correction">現地で決めた位置の補正 (x, z)。地図の座標で足す。signs.json の correctionX, correctionZ。</param>
        public static SignAlignment FromDetection(
            Vector2 signMap,
            float signAzimuthDeg,
            Vector2 detectedPosition,
            Vector2 detectedNormal,
            float correctionYawDeg = 0f,
            Vector2 correction = default)
        {
            float yaw = Azimuth.Of(detectedNormal) - (signAzimuthDeg + correctionYawDeg);
            return new SignAlignment(Azimuth.Normalize(yaw), signMap + correction, detectedPosition);
        }

        /// <summary>地図の点 (x, z) を AR の (x, z) にする。</summary>
        public Vector2 MapToAr(Vector2 map) => Azimuth.RotateY(map - SignMap, YawDeg) + SignAr;

        /// <summary>地図の点 (x, z) を、高さ y の AR の点にする。</summary>
        public Vector3 MapToAr(Vector2 map, float y)
        {
            var p = MapToAr(map);
            return new Vector3(p.x, y, p.y);
        }

        /// <summary>
        /// RouteRenderer を置く位置と回転。RouteRenderer は経路の最初の点を原点にして描くので、
        /// その点の AR 上の位置と、この変換の回転を transform に入れれば、地図の経路が AR に重なる。
        /// </summary>
        /// <param name="firstPointOnMap">経路の最初の点 (x, z)。RouteData.points[0]。</param>
        /// <param name="floorY">床の高さ（AR の y）。</param>
        public Pose RouteRendererPose(Vector2 firstPointOnMap, float floorY) =>
            new Pose(MapToAr(firstPointOnMap, floorY), Rotation);
    }
}
