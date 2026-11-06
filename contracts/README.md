# 役割の間の取り決め

別々に作っても最後につながるように、受け渡しの形をここに置きます。サンプルと仕様はサーバーから自動で作っているので、手で直さないでください。

## つなぎ目

| つなぎ目 | 作る側 | 使う側 | 形 | 確かめ方 |
|---|---|---|---|---|
| 現地 → データ | 現地担当 | サーバー、Unity | `server/station_data/signs.json`（[signs.schema.json](signs.schema.json)） | `pack.build` がスキーマを確かめる |
| データ → サーバー | データ担当 | サーバー | `server/station_data/pack.json` | `pytest` |
| サーバー → Unity（経路） | サーバー担当 | Unity 担当 | `POST /v1/route`（[サンプル](samples/route_ok_from_sign.json)） | `pytest` が `RouteData.cs` と突き合わせる |
| サーバー → Unity（行き先） | サーバー担当 | Unity 担当 | `POST /v1/destination`（[サンプル](samples/destination_ok.json)） | [openapi.json](openapi.json) |
| サーバー → Unity（駅の基本データ） | サーバー担当 | Unity 担当 | `GET /v1/station`（[サンプル](samples/station.json)）。同じものをアプリに同梱できる | [openapi.json](openapi.json) |
| 案内板 → 線 | Unity 担当 | Unity 担当 | 下の「位置合わせ」と [alignment_vectors.json](alignment_vectors.json) | 同じ答えが出るかを Unity 側のテストで見る |

Unity 担当は、サーバーがなくても `samples/` の JSON を読めば画面を作れます。サーバー担当は、Unity がなくても http://localhost:8090 のページで経路を確かめられます。

## 座標

- 単位はメートル。固定原点は北緯 35.6900 度、東経 139.7000 度
- x = 東、z = 北、y = 上（Unity と同じ左手系）
- 階は `ordinal`。-1 は地下1階、2 は2階。坂や階段の途中の点は小数になる（RouteData.cs の高さの計算に使う）
- 方位は北から時計回りの度。案内板の `azimuthDeg` は「板面の外向き」の方位で、板に向き合う人が見ている方向とは 180 度違う

## API の決まり

- JSON は Unity の `JsonUtility` で読めるようにしています。一番外側は必ずオブジェクトで、`null` は返しません。文字列がないときは `""`、距離が不明なときは `-1` です
- エラーは `{"error": {"code": "...", "message": "..."}}` です。アプリは `code` で分岐します

| code | HTTP | 意味 | アプリの動き |
|---|---|---|---|
| `unknown_sign`、`unknown_gate` | 404 | ID がない | アプリとサーバーのデータの版が違う可能性がある |
| `no_route` | 404 | 経路がない | 別の行き先を選んでもらう |
| `invalid_request` | 422 | 入力が不正 | 開発時に直す |
| `rate_limited` | 429 | 回数の上限 | 同梱の経路に切り替える |
| `internal_error` | 500 | サーバーの不具合 | 同梱の経路に切り替える |
| `unknown_floor`、`http_error` | 404 など | ページ用の API や存在しないパス | アプリでは出ない |

- 行き先の `status` は次のとおりです

| status | アプリの動き |
|---|---|
| `ok` | `candidates` の先頭へ案内する |
| `ambiguous` | `candidates` から選んでもらう |
| `out_of_scope`、`unavailable` | 行き先の一覧を出す |

- `verticalMoves` は階段、エスカレーター、エレベーターと、階が変わる坂（`slope`）の区間です。階段は同じ階の段差も含みます。階が変わるかは `points` の `ordinal` で見ます
- `turns` は階の移動の区間を除いた曲がり角です。2D 表示の「次の曲がり角」と振動に使います
- `ok` は `candidates` が複数でも先頭が最も近い改札です。出発が分からないときは `ambiguous` になります
- 経路は出発時に1回取得して端末に保持します。2秒以内に取れなければ同梱の経路を使います。かざし直しのたびに取得し直しません

## 位置合わせ

案内板を認識したら、一覧の値と組み合わせて地図と AR の座標を結びます。`app/alignment.py` が基準の実装で、[alignment_vectors.json](alignment_vectors.json) に入力と答えがあります。Unity 側の実装はこの答えと一致させてください。`expectedYawDeg` は -180 度より大きく 180 度以下にそろえています。比べるときは `mapToAr` の座標を見るのが確実です。

```
yaw = （認識した法線の方位） - （azimuthDeg + correctionYawDeg）
地図の点 P の AR 座標 = 回転(yaw) × (P - 案内板の位置) + 認識した位置
```

`RouteRenderer` は `points[0]` を原点にして描くので、その位置と回転に次を入れます。次のコードは Unity なしで書いた参考で、コンパイルしていません。ARTrackedImage の法線がどの軸かも未確認なので、実機で軸を描画して確かめてください。

```csharp
var normal = Vector3.ProjectOnPlane(image.transform.up, Vector3.up).normalized;
var detectedAzimuth = Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg;
var yaw = detectedAzimuth - (sign.azimuthDeg + sign.correctionYawDeg);
var rotation = Quaternion.Euler(0f, yaw, 0f);
var signOnMap = new Vector3(sign.x + sign.correctionX, 0f, sign.z + sign.correctionZ);
var first = new Vector3(route.points[0].x, 0f, route.points[0].z);
var offset = rotation * (first - signOnMap);
var position = new Vector3(image.transform.position.x + offset.x, floorY, image.transform.position.z + offset.z);
routeRenderer.transform.SetPositionAndRotation(position, rotation);
```

守ること（ARCore の公式と設計案から）:

- 位置と向きは、板が FULL_TRACKING の間に安定してから確定する
- 回転は水平だけを使う。上方向は重力で決める
- カメラが板の裏側にある検出は採用しない
- 追跡が止まったら線を消す。追跡が戻っても、かざし直すまで線を出さない

## 変更するとき

API の形を変えたら、次で作り直してコミットします。`pytest` は、サンプルとサーバーがずれていると失敗します。

```
cd server
.venv/bin/python -m app.export_contracts
.venv/bin/python -m pytest
```

`RouteData.cs` のフィールドを変えた場合も `pytest` で分かります。

## まだ仮のもの

- 案内板はすべて現地確認前の仮データです（`confidence` が `仮`）
- 路線と改札の対応は公式の構内図と照合していません（`confirmed` が `false`）
- Unity 側の案内板の認識、API の取得、追跡が止まったときの処理はまだありません
- Cloud Run には配置していません。`vertex` の接続は実環境で試していません
