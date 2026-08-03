# Amazon Music SMTC Bridge

Windows 版 Amazon Music は SMTC (System Media Transport Controls) に一部の情報しか渡さないため、
[Pano Scrobbler](https://github.com/kawaiiDango/pano-scrobbler) のような SMTC ベースの
スクロブラーが正しく動作しません。

このアプリは、**完全なメタデータを持つ SMTC セッションを別途発行する**ことでこれを補います。

---

## 何が問題なのか（実測）

対象: Amazon Music 9.5.2.2478 (Microsoft Store 版)

再生中に Amazon Music が SMTC に出している内容:

| フィールド | Amazon Music | 本アプリ |
|---|---|---|
| Title | ❌ 基本的に空（一部の曲のみ入る） | ✅ 取得 |
| Artist | ✅ | ✅ |
| AlbumTitle | ❌ 常に空 | ✅ |
| AlbumArtist | ❌ 常に空 | ✅ |
| Thumbnail (アートワーク) | ❌ 常に無し | ✅ |
| Timeline (曲の長さ) | ❌ 常に `00:00:00` | ✅ |

Title が空の状態ではスクロブラーは曲を特定できず、スクロブルできません。

> **Title について**: 基本的に空です。ごく一部の曲では埋まっていることがありますが、
> 3分間の低速再生テスト（25〜30秒に1回の曲送り）では**一度も入りませんでした**。
> AlbumTitle / AlbumArtist / アートワーク / 曲の長さは**全サンプルで欠落**しています。

## 仕組み

Amazon Music のセッションを修正することは**できません**。SMTC のメタデータは発行元プロセスの
所有物で、`GlobalSystemMediaTransportControlsSessionManager` は読み取りと再生操作しか提供しません。
そこで本アプリは自分名義のセッションを立てます。

メタデータの取得元は 2 通りあり、既定では使えるほうが自動で選ばれます。

**CDP（既定・推奨）** — Amazon Music は CEF (Chromium 79) 製なので、`--remote-debugging-port`
付きで起動し直せばレンダラを直接読めます。取得元は内部の Vuex ストアです。

```
Amazon Music のレンダラ (CDP)  ──> 曲名 / アーティスト / アルバム / アートワーク
                                    / 再生位置 / 曲の長さ（すべてミリ秒精度）
Amazon Music の SMTC セッション ──> 再生・一時停止の状態
                                        │
                                        ▼
                          本アプリの SMTC セッション（完全版）
                                        │
                          操作ボタンは Amazon Music に中継
```

**通知（フォールバック）** — 従来方式。Amazon Music を再起動せずに済みますが、
Amazon Music にフォーカスがある間は曲が更新されません（後述）。

```
Amazon Music の曲変更トースト ──> 曲名 / アーティスト / アルバム / アートワーク
Amazon Music の SMTC セッション ──> 再生・一時停止の状態
Amazon Music の Local Storage  ──> 曲の長さ（ベストエフォート）
```

> **アルバム名について**: CDP ではストアの `track.album.name` を読みます。
> プレイヤー下部に表示されている 2 番目のリンクは**再生コンテキスト（プレイリスト名）**であって
> アルバムではないため、意図的に読んでいません。

---

## 必要なもの

- Windows 10 1809 以降 / Windows 11
- Amazon Music（Microsoft Store 版・exe 版のどちらでも）

通知フォールバックを使う場合（既定の `自動` と `通知のみ`）は追加で:

- **Amazon Music の Windows 通知が ON であること**（設定 > システム > 通知 > Amazon Music）
- 初回起動時の「通知へのアクセス」許可

取得方式を `デバッグポートのみ` にすると、通知は一切使われず上記の許可も要求されません。

## インストール

Release から `AmazonMusicSmtc-v<バージョン>.msix` と `AmazonMusicSmtc-v<バージョン>.cer` の両方をダウンロードします。

自己署名のため、先に証明書を信頼する必要があります。**管理者権限の PowerShell** で:

```powershell
Import-Certificate -FilePath .\AmazonMusicSmtc-v1.0.0.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

続いて通常の PowerShell で:

```powershell
Add-AppxPackage .\AmazonMusicSmtc-v1.0.0.msix
```

## Pano Scrobbler の設定（重要）

インストール後は SMTC セッションが**2つ**並びます。何もしないと二重スクロブルになります。

1. Pano Scrobbler のアプリ一覧で **Amazon Music を無効化**
2. **Amazon Music SMTC Bridge を有効化**

Pano Scrobbler は未知のアプリを検出すると通知を出すので、そこから有効化できます。

## 設定

タスクトレイのアイコンを右クリック:

- **ログを表示** — 取得状況のログ（不具合報告用）
- **曲情報の取得方式**
  - `自動`（既定）— デバッグポートが使えれば CDP、駄目なら通知
  - `デバッグポートのみ` — CDP のみ。通知リスナーを一切使わない
  - `通知のみ` — 従来方式。Amazon Music を再起動せず、デバッグポートも開かない
- **デバッグポート付きで再起動** — 手動で入れ替える
- **常にデバッグポート付きで動かす** — 既定 ON。デバッグポート無しで動いている
  Amazon Music を見つけたら起動し直します（ユーザーが自分で起動した場合も含む）
- **読み取った通知を非表示** — 既定 OFF。Amazon Music の通知は消えずに
  通知センターに溜まり続けるため、処理後に自動で消したい場合に ON にします
- **ジャケットをオフライン用に保存** — 既定 OFF。ON にするとダウンロードしたジャケットを
  ローカルに保存し、オフラインでも表示できるようにします（最大 100 件）

自動起動は既定で有効です。設定 > アプリ > スタートアップ から切り替えられます。

### デバッグポートについて（重要）

CDP を使う設定では Amazon Music が `--remote-debugging-port` 付きで動きます。この状態では
**同じ PC 上の任意のプロセスが認証なしで Amazon Music のレンダラに接続し、
ログイン済みセッションで任意の JavaScript を実行できます。**

緩和として、慣例的な 9222 ではなく動的レンジからランダムに選んだポートを初回に決めて
`settings.json` に保存します。ただしこれは**緩和であって解決ではありません**
（ポートスキャンと `/json/version` で特定は可能です）。
これが許容できない場合は `通知のみ` を選んでください。

---

## 制限事項

CDP 方式（既定）:

- **Amazon Music を起動し直します。** デバッグポート無しで動いていると入れ替えるため、
  再生が中断され、再生位置は曲の先頭に戻ります。ユーザーが自分で起動した直後も対象です
  （検知から接続完了まで実測 13 秒）。煩わしい場合は
  **常にデバッグポート付きで動かす** を OFF にしてください
- **デバッグポートのセキュリティ上のトレードオフ**があります（上記）
- **Amazon Music の内部実装に依存します。** 現在は CEF 79 / Vue 2 の Vuex ストアを読んでいます。
  アプリ更新でストアが取れなくなった場合は DOM 読み取りに落ちますが、
  そのときは**アルバム名が空になります**（DOM にはプレイリスト名しか無いため、
  誤った値を出すより空にしています）。さらに駄目なら通知方式に落ちます
- **曲変更の検知は最大 2 秒遅れます。** 起動中のアプリにプロトコルドメインを有効化したり
  スクリプトを常駐させたりすると Amazon Music が起動しなくなるため、
  読み取り専用のポーリングに徹しています

通知方式:

- **Amazon Music の通知が OFF だと動きません**
- **Amazon Music のウィンドウを前面にしたままだと曲が更新されません。** Amazon Music は
  自分にフォーカスがあるとき曲変更のトーストを**発行しません**（表示されないのではなく、
  作られていません）。この状態では Amazon Music 自身の SMTC も曲名を出さないため、
  取得できる情報が何もありません。**これが CDP 方式を用意した理由です**
- **曲の長さはベストエフォート。** Amazon Music のカタログキャッシュに該当曲が無い場合は
  長さ無しになります
- **再生位置は推定です。** 曲変更時刻からの経過時間で計算しているため、
  Amazon Music 側でシークすると次の曲変更まで位置がずれます

共通:

- **シーク操作は受け付けません。** CDP 方式では再生位置を正確に*表示*できますが、
  スクロバーからのシーク要求を Amazon Music に中継する機能はありません
- 集中モード（フォーカスアシスト）有効時の挙動は未検証です

## 検証状況

実機で確認済み（Amazon Music 9.5.2.0 / CEF 79.1.38 / Store 版）:

- **Amazon Music を最前面に固定したまま曲送りしても追随すること**。同じ時間帯に
  通知用アートワークファイルの更新時刻が一切動かないこと（＝トーストが出ていないこと）も併せて確認
- アルバム名がプレイリスト名ではなく実アルバムになること
- CDP からの再生位置・曲の長さが SMTC タイムラインに乗ること
- ジャケットの取得と表示（CDN から取得、認証不要）
- **Amazon Music を通常起動したときの自動入れ替え**（検知から接続完了まで 13 秒）
- 既に別のポートで動いている Amazon Music を `DevToolsActivePort` から発見して再利用すること
  （不要な再起動をしない）
- 起動に失敗し続けたときに 3 回で断念してログを残すこと
- `KeepArtworkCache` が既定 OFF のときディスクに何も書かないこと
- Amazon Music が動いていないときにブリッジが勝手に起動しないこと
- 通知からの曲名・アーティスト・アルバム・アートワーク取得
- 起動時のキャッチアップ（起動直後に再生中の曲を即反映）
- 再生・一時停止状態の同期
- ブリッジ側セッションからの Pause / Play が Amazon Music に中継されること
- 曲の長さの取得と SMTC タイムラインへの反映
- 署名済み `.msix` の生成
- ログウィンドウを閉じても常駐が維持されること（× / Alt+F4 の両経路を実測）

未確認:

- 署名済み `.msix` のクリーンな環境でのインストール（開発機では登録済みレイアウトで検証）
- Pano Scrobbler での実際のスクロブル
- Amazon Music の非 Store 版（exe 版）— パス解決は実装済みだが未実機検証。
  なお CDP 方式の起動経路は Store 版（MSIX）専用です
- ポッドキャスト／ビデオ再生時のメタデータ
- 長時間の連続運用（数時間規模）での安定性

---

## ソースからのビルド

必要: .NET 10 SDK、開発者モード ON

```powershell
.\tools\build-dev.ps1
```

署名なしでパッケージ ID を得られる `Add-AppxPackage -Register` を使うため、開発中の反復では
証明書は不要です（`userNotificationListener` capability はパッケージ ID が無いと宣言できないため、
未パッケージの exe では成立しません）。

配布用の署名済みパッケージ:

```powershell
.\tools\pack-release.ps1 -Version 1.0.0 -OutputName AmazonMusicSmtc-v1.0.0
```

### リリース（GitHub Actions）

GitHub の **Actions > リリース > Run workflow** から手動で実行します。バージョンの指定方法は2通り:

- **新規タグを作成**: ブランチを選び、「バージョン」に `v1.0.0` と入力（`v` を省いて `1.0.0` と入力しても同じで、
  タグは必ず `v1.0.0` になります）
- **既存タグを選択**: 「Use workflow from」で既存タグを選び、「バージョン」は空のまま

コマンドラインからも実行できます（ワークフロー名ではなくファイル名で指定します）:

```powershell
gh workflow run release.yml -f version=1.0.0
```

ワークフローがビルド・署名・パッケージングを行い、`AmazonMusicSmtc-v1.0.0.msix` と
`AmazonMusicSmtc-v1.0.0.cer` を Release に添付します。

署名には以下の Secrets に登録された固定の証明書（`CN=AmazonMusicSmtc`、有効期限 2036-08-01）を使います:

- `SIGNING_PFX_BASE64` — `.pfx` を Base64 にしたもの
- `SIGNING_PFX_PASSWORD` — その `.pfx` のパスワード

これらが未設定だと実行のたびに新しい自己署名証明書が作られ、ユーザーは更新のたびに別の証明書を
信頼し直すことになります。証明書を差し替える場合は両方をまとめて更新してください:

```powershell
$pw  = '<新しいパスワード>'
$pfx = "$env:TEMP\signing.pfx"
Export-PfxCertificate -Cert Cert:\CurrentUser\My\<thumbprint> -FilePath $pfx `
  -Password (ConvertTo-SecureString $pw -Force -AsPlainText)
gh secret set SIGNING_PFX_BASE64   --body ([Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)))
gh secret set SIGNING_PFX_PASSWORD --body $pw
Remove-Item $pfx
```

SMTC の中身を確認する検証スクリプト:

```powershell
.\tools\dump-smtc.ps1
```
