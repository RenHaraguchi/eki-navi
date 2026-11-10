using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 撮った画像を、看板の文字がまっすぐになる向きに回す量。
    /// アプリは縦向きに固定しているので、端末を横に倒して撮ると、画像の中の看板は 90 度回っている。
    /// Gemini には回してまっすぐにした画像を送り、答えの座標は元の画像の座標に戻してから使う。
    /// </summary>
    public enum ImageTurn
    {
        /// <summary>回さない（縦向きで撮った）。</summary>
        None,
        /// <summary>画像を時計回りに 90 度回す（端末の上端を右に倒して撮った）。</summary>
        Clockwise,
        /// <summary>画像を反時計回りに 90 度回す（端末の上端を左に倒して撮った）。</summary>
        CounterClockwise,
        /// <summary>画像を 180 度回す（端末を逆さまにして撮った）。</summary>
        Half,
    }

    public static class ImageTurns
    {
        /// <summary>
        /// 撮った瞬間のカメラの向きから、画像を回す量を決める。
        /// 画像の中で世界の上（重力の反対）がどちらを向いているかで決め、ちょうど斜め 45 度の境目は縦向きを優先する。
        /// </summary>
        public static ImageTurn Choose(Quaternion cameraRotation)
        {
            // カメラから見た世界の上。カメラの座標は x = 画面の右、y = 画面の上
            var up = Quaternion.Inverse(cameraRotation) * Vector3.up;
            float x = up.x, y = up.y;
            // 真上か真下に近い向きでは、少しの傾きで横向きと取り違えるので、縦とみなす
            var forward = cameraRotation * Vector3.forward;
            if (Mathf.Abs(forward.y) > 0.95f || x * x + y * y < 1e-4f) return ImageTurn.None;
            if (Mathf.Abs(y) >= Mathf.Abs(x)) return y >= 0f ? ImageTurn.None : ImageTurn.Half;
            // 世界の上が画面の右に見える → 画像を反時計回りに回すと上が上に来る
            return x > 0f ? ImageTurn.CounterClockwise : ImageTurn.Clockwise;
        }

        /// <summary>回した画像の点（左上が原点の 0〜1）を、元の画像の点に戻す。</summary>
        public static Vector2 ToOriginal(ImageTurn turn, Vector2 turned)
        {
            switch (turn)
            {
                case ImageTurn.Clockwise:
                    // 元の (x, y) は、時計回りに回すと (1 − y, x) に来る
                    return new Vector2(turned.y, 1f - turned.x);
                case ImageTurn.CounterClockwise:
                    // 元の (x, y) は、反時計回りに回すと (y, 1 − x) に来る
                    return new Vector2(1f - turned.y, turned.x);
                case ImageTurn.Half:
                    return new Vector2(1f - turned.x, 1f - turned.y);
                default:
                    return turned;
            }
        }

        /// <summary>元の画像の点を、回した画像の点にする（<see cref="ToOriginal"/> の逆）。</summary>
        public static Vector2 ToTurned(ImageTurn turn, Vector2 original)
        {
            switch (turn)
            {
                case ImageTurn.Clockwise:
                    return new Vector2(1f - original.y, original.x);
                case ImageTurn.CounterClockwise:
                    return new Vector2(original.y, 1f - original.x);
                case ImageTurn.Half:
                    return new Vector2(1f - original.x, 1f - original.y);
                default:
                    return original;
            }
        }

        /// <summary>
        /// 画素を回す。画素の並びは Texture2D.GetPixels32 と同じ（左下が先頭で、行は下から上）。
        /// </summary>
        /// <param name="source">元の画素。</param>
        /// <param name="width">元の横の画素数。</param>
        /// <param name="height">元の縦の画素数。</param>
        /// <param name="turn">回す量。</param>
        /// <param name="turnedWidth">回したあとの横の画素数。</param>
        /// <param name="turnedHeight">回したあとの縦の画素数。</param>
        public static Color32[] Rotate(Color32[] source, int width, int height, ImageTurn turn,
            out int turnedWidth, out int turnedHeight)
        {
            bool swap = turn == ImageTurn.Clockwise || turn == ImageTurn.CounterClockwise;
            turnedWidth = swap ? height : width;
            turnedHeight = swap ? width : height;
            if (turn == ImageTurn.None) return source;
            var result = new Color32[source.Length];
            int tw = turnedWidth;
            for (int ay = 0; ay < height; ay++)
            {
                int row = ay * width;
                for (int ax = 0; ax < width; ax++)
                {
                    int tx, ty;
                    switch (turn)
                    {
                        case ImageTurn.Clockwise:
                            tx = ay;
                            ty = width - 1 - ax;
                            break;
                        case ImageTurn.CounterClockwise:
                            tx = height - 1 - ay;
                            ty = ax;
                            break;
                        default: // Half
                            tx = width - 1 - ax;
                            ty = height - 1 - ay;
                            break;
                    }
                    result[ty * tw + tx] = source[row + ax];
                }
            }
            return result;
        }
    }
}
