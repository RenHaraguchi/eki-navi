"""案内板を認識したときの、地図と AR の座標の結び付け（基準となる実装）。

サーバーでは使わない。Unity 側の実装が同じ答えを出すかを確かめるために置いている。
contracts/alignment_vectors.json はここから作る。

座標は Unity に合わせて、x = 東、z = 北、y = 上の左手系。方位は北から時計回り。
"""

from __future__ import annotations

import math
from dataclasses import dataclass


@dataclass(frozen=True)
class Alignment:
    """地図から AR への変換。yaw_deg だけ回してから足す。"""

    yaw_deg: float
    sign_map: tuple[float, float]
    sign_ar: tuple[float, float]

    def map_to_ar(self, x: float, z: float) -> tuple[float, float]:
        """地図の点を AR の座標にする。

        Args:
            x: 地図の x（東）。
            z: 地図の z（北）。

        Returns:
            AR の (x, z)。
        """
        rx, rz = rotate_y((x - self.sign_map[0], z - self.sign_map[1]), self.yaw_deg)
        return rx + self.sign_ar[0], rz + self.sign_ar[1]


def rotate_y(vector: tuple[float, float], yaw_deg: float) -> tuple[float, float]:
    """Unity の Quaternion.Euler(0, yaw, 0) と同じ回転をする。

    Args:
        vector: (x, z)。
        yaw_deg: 回転の角度。正は上から見て時計回り。

    Returns:
        回転後の (x, z)。
    """
    rad = math.radians(yaw_deg)
    cos, sin = math.cos(rad), math.sin(rad)
    return vector[0] * cos + vector[1] * sin, -vector[0] * sin + vector[1] * cos


def azimuth_of(vector: tuple[float, float]) -> float:
    """水平のベクトルの方位を返す。

    Args:
        vector: (x, z)。

    Returns:
        北から時計回りの角度。0 以上 360 未満。
    """
    return math.degrees(math.atan2(vector[0], vector[1])) % 360.0


def _normalize_deg(angle: float) -> float:
    """角度を -180 度より大きく 180 度以下にする。"""
    wrapped = (angle + 180.0) % 360.0 - 180.0
    return 180.0 if wrapped == -180.0 else wrapped


def align_to_sign(
    sign_map: tuple[float, float],
    sign_azimuth_deg: float,
    detected_position: tuple[float, float],
    detected_normal: tuple[float, float],
    correction_yaw_deg: float = 0.0,
    correction: tuple[float, float] = (0.0, 0.0),
) -> Alignment:
    """案内板の認識結果から、地図と AR の変換を作る。

    Args:
        sign_map: 一覧に登録した案内板の位置 (x, z)。
        sign_azimuth_deg: 一覧に登録した板の外向きの方位。
        detected_position: AR で認識した板の位置 (x, z)。
        detected_normal: AR で認識した板の外向きの法線を水平にしたもの (x, z)。
        correction_yaw_deg: 現地で決めた向きの補正。
        correction: 現地で決めた位置の補正 (x, z)。地図の座標系で足す。

    Returns:
        地図から AR への変換。
    """
    yaw = azimuth_of(detected_normal) - (sign_azimuth_deg + correction_yaw_deg)
    return Alignment(
        yaw_deg=_normalize_deg(yaw),
        sign_map=(sign_map[0] + correction[0], sign_map[1] + correction[1]),
        sign_ar=detected_position,
    )
