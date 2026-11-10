# サーバーを Cloud Run に出す手順

アプリの「Scan sign」（案内板を Gemini で見分ける機能）は、サーバーの `POST /v1/sign/identify` を呼びます。PC につながずに駅で使うには、サーバーをインターネット上に出す必要があります。この手順は、チームの Google Cloud のプロジェクトにサーバーを出し、アプリをその URL につなぐまでをまとめたものです。

Gemini は Cloud Run のサービスアカウントの権限で Vertex AI から呼びます（`GEMINI_BACKEND=vertex`）。API キーは使わないので、鍵を置く必要はありません。

## 出す前に確かめること

- 出すブランチ: `develop`。PR #13（案内板を Gemini で見分ける）と #14（経路パックの作り直しと看板のデータ）がマージ済みであること。アプリのビルドで URL を入れるには、推測航法の PR もマージ済みであること
- 使う PC に `gcloud` CLI が入っていて、プロジェクトのオーナーか編集者のアカウントでログインしていること（`gcloud auth login`）
- プロジェクトに請求先アカウント（チームのクレジット）がつながっていること
- 予算アラートを設定していること。アラートは通知だけで、自動では止まりません

以下の `<PROJECT_ID>` はプロジェクトの ID に読み替えてください。

## 1. プロジェクトを選ぶ

```
gcloud config set project <PROJECT_ID>
```

## 2. 使う API を有効にする

```
gcloud services enable run.googleapis.com cloudbuild.googleapis.com \
    artifactregistry.googleapis.com aiplatform.googleapis.com
```

## 3. Cloud Run が Gemini を呼べるようにする

Cloud Run は何も指定しないと Compute Engine の既定のサービスアカウントで動きます。このアカウントに Vertex AI を使う権限を付けます。

```
PROJECT_NUMBER=$(gcloud projects describe <PROJECT_ID> --format='value(projectNumber)')
gcloud projects add-iam-policy-binding <PROJECT_ID> \
    --member="serviceAccount:${PROJECT_NUMBER}-compute@developer.gserviceaccount.com" \
    --role="roles/aiplatform.user"
```

新しく作ったプロジェクトでは、同じアカウントにビルドの権限がなく、手順 4 のビルドが権限不足で失敗します。先に付けておきます。

```
gcloud projects add-iam-policy-binding <PROJECT_ID> \
    --member="serviceAccount:${PROJECT_NUMBER}-compute@developer.gserviceaccount.com" \
    --role="roles/cloudbuild.builds.builder"
```

## 4. 出す

リポジトリの `server` フォルダで実行します。`Dockerfile` でイメージを作り、経路パック（`station_data/pack.json`）を焼き込みます。初回は 5〜10 分ほどかかります。

```
cd server
gcloud run deploy eki-navi --source . --region asia-northeast1 \
    --allow-unauthenticated --max-instances 3 \
    --set-env-vars GEMINI_BACKEND=vertex,GOOGLE_CLOUD_PROJECT=<PROJECT_ID>,GOOGLE_CLOUD_LOCATION=global
```

- `--max-instances 3` は費用の上限のために必ず付けます
- 回数の上限はインスタンスごとに `GEMINI_RATE_PER_MINUTE`（1 分あたり、既定 60 回）と `GEMINI_DAILY_LIMIT`（1 日あたり、既定 1000 回）で決まります。変えるときは `--set-env-vars` に足します
- アプリからの要求だけを受け付けるには、Play Integrity API を有効にし（`gcloud services enable playintegrity.googleapis.com`）、`INTEGRITY_MODE=log` で出してから、ログで `Play Integrity: ... ok=True` を確かめて `enforce` に切り替えます。切り替えは `gcloud run services update eki-navi --region asia-northeast1 --update-env-vars INTEGRITY_MODE=enforce` だけで、ビルドし直しは要りません
- 終わると、`Service URL: https://eki-navi-xxxx.asia-northeast1.run.app` のように URL が出ます。これをアプリに入れます

## 5. 動いているか確かめる

```
curl -s -o /dev/null -w '%{http_code}\n' https://<サービスの URL>/
```

`200` が返れば起動しています。ブラウザで `https://<サービスの URL>/` を開くと、経路と行き先の判定を試せるページが出ます。

`/healthz` は使えません。Cloud Run は `z` で終わる一部のパスを予約していて、アプリに届く前に Google の入口が 404 を返します。

案内板の判定は看板の写真を送って確かめます（Mac の例。`photo.jpg` は看板の写真）。

```
base64 -i photo.jpg | tr -d '\n' \
  | jq -Rs '{imageBase64: ., mimeType: "image/jpeg", candidateSignIds: ["S-B1-H02-A", "S-B1-H03"]}' \
  | curl -s -X POST -H 'Content-Type: application/json' -d @- https://<サービスの URL>/v1/sign/identify
```

- `"status":"ok"` と `"signId"` が返れば、Gemini まで動いています
- `"status":"not_found"` は、写真の看板が候補にないか、文字が読めなかったときです。`readText` に Gemini が読んだ文字が入っています
- `"status":"unavailable"` は、Gemini を呼べなかったときです。下の「うまくいかないとき」を見てください

## 予算アラートを付ける

アラートは通知だけで、自動では止まりませんが、思わぬ利用に気づけます。Cloud コンソールの「お支払い」→「予算とアラート」→「予算を作成」で、対象をこのプロジェクトにして、金額（例: 3,000 円）と通知のしきい値（50%、90%、100% など）を決めます。

URL を知っている人は誰でも案内板の判定（Gemini）を呼べるので、URL は GitHub やリポジトリに書かず、チームの中だけで共有してください。

## 6. アプリをサーバーの URL につないでビルドする

環境変数 `EKINAVI_SERVER_URL` にサービスの URL を入れてから、案内板の経路デモのシーンを作ると、「Scan sign」の送り先にその URL が入ります。

Unity のエディターから作るとき（Mac）は、環境変数を渡すために、ターミナルからエディターを起動します（Unity Hub から開くと環境変数が渡りません）。

```
EKINAVI_SERVER_URL=https://<サービスの URL> \
  /Applications/Unity/Hub/Editor/6000.3.28f1/Unity.app/Contents/MacOS/Unity -projectPath .
```

メニュー「EkiNavi > 案内板の経路デモのシーンを作る」を実行してからビルドします。作ったシーン（`SignRouteDemo.unity`）には URL が書き込まれるので、コミットしないでください。シーンを作ったあとに、Route の `SignVisionAligner` の Server Url に URL が入っていることを確かめてください。環境変数を使わずに、ここへ直接 URL を入れても構いません（シーンを作り直すと空に戻ります）。

コマンド 1 つで APK まで作るときは、次のようにします。

```
EKINAVI_SERVER_URL=https://<サービスの URL> \
  Unity -batchmode -quit -buildTarget Android -projectPath . \
  -executeMethod EkiNavi.EditorTools.SignDemoBatchBuilder.BuildApk -logFile -
```

## うまくいかないとき

ログは次のコマンドで見られます。

```
gcloud run services logs read eki-navi --region asia-northeast1 --limit 50
```

- `案内板を見分けられませんでした` と出て、Gemini の呼び出しが `HTTP 403` のとき: 手順 3 の権限が付いていません
- `HTTP 404` のとき: そのモデルが指定した場所（`GOOGLE_CLOUD_LOCATION`）で使えません。`GOOGLE_CLOUD_LOCATION=us-central1` にして出し直すか、`GEMINI_MODEL` で別のモデルを指定してください
- `HTTP 429` のとき: Gemini の回数の上限です。時間をおいて試してください
- アプリで「Cannot check the sign now」と出るとき: アプリの Server Url が空か、間違っています。開発用の表示（画面の左上を素早く 3 回たたく）を出すと、詳しい理由が出ます

## 止めるとき

使わない間の費用を止めるには、サービスを消します。消しても、手順 4 でいつでも出し直せます。

```
gcloud run services delete eki-navi --region asia-northeast1
```

## 案内板のデータの置き場所

地下1階の案内板のデータは、サーバーの経路パック（`server/station_data/pack.json`）にだけ置き、リポジトリにはコミットしません。アプリは起動時に `GET /v1/station` で案内板の一覧を読みます。読めないときだけ、同梱の `Assets/EkiNavi/Data/signs.json`（少ない枚数）を使います。

リポジトリのままサーバーを出し直すと、案内板が同梱の枚数に戻ります。出し直すときは、手元の `pack.json` と `signs.json` を `server/station_data/` に置いてから `gcloud run deploy` してください（コミットはしません）。
