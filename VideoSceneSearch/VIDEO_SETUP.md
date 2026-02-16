# 動画ファイルの配置方法

## 動画ファイルの配置場所

動画ファイルは `VideoSceneSearch/wwwroot/videos/` フォルダに配置してください。

```
VideoSceneSearch/
├── wwwroot/
│   ├── videos/
│   │   ├── ski-for-two.mp4          # 実際の動画ファイル
│   │   ├── sample1.mp4
│   │   ├── sample2.mp4
│   │   └── thumbnails/              # サムネイル画像（オプション）
│   │       ├── ski-for-two.jpg
│   │       ├── sample1.jpg
│   │       └── sample2.jpg
│   └── css/
└── videomapping.json                # 動画マッピング設定
```

## 動画マッピング設定（videomapping.json）

`videomapping.json` ファイルで、Video ID と実際のファイルパスをマッピングします：

```json
{
  "VideoMapping": {
    "bycB6smq8k": {
      "title": "012 - Ski For Two (1944)",
      "file": "/videos/ski-for-two.mp4",
      "thumbnail": "/videos/thumbnails/ski-for-two.jpg"
    },
    "video1": {
      "title": "サンプル動画 1",
      "file": "/videos/sample1.mp4"
    }
  }
}
```

## セットアップ手順

### 1. 動画ファイルを配置

実際の動画ファイル（MP4形式推奨）を `wwwroot/videos/` フォルダにコピーしてください。

```powershell
# PowerShellの例
Copy-Item "C:\path\to\your\video.mp4" -Destination "VideoSceneSearch\wwwroot\videos\sample1.mp4"
```

### 2. videomapping.json を編集

エージェントが返す `videoId` に対応するファイルパスを設定します：

```json
{
  "VideoMapping": {
    "実際のVideoID": {
      "title": "動画タイトル",
      "file": "/videos/ファイル名.mp4"
    }
  }
}
```

### 3. アプリケーションを起動

```powershell
cd VideoSceneSearch
dotnet run
```

ブラウザで `http://localhost:5062` にアクセスして、検索を試してください。

## サンプル動画を使用する場合

動画ファイルがない場合は、無料のサンプル動画を使用できます：

### オンラインサンプル動画（デフォルト設定）

アプリケーションは、動画ファイルが見つからない場合、自動的にGoogle提供のサンプル動画を使用します：

- Big Buck Bunny
- Elephant's Dream
- For Bigger Blazes

### ローカルサンプル動画のダウンロード

```powershell
# Big Buck Bunnyをダウンロード
Invoke-WebRequest -Uri "https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4" `
    -OutFile "VideoSceneSearch\wwwroot\videos\sample1.mp4"

# Elephant's Dreamをダウンロード
Invoke-WebRequest -Uri "https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4" `
    -OutFile "VideoSceneSearch\wwwroot\videos\sample2.mp4"
```

## トラブルシューティング

### 動画が再生されない

1. **ファイルパスを確認**
   - `videomapping.json` のパスが正しいか確認
   - ファイルが `wwwroot/videos/` に存在するか確認

2. **ブラウザの開発者ツールで確認**
   - F12 → Console タブでエラーメッセージを確認
   - Network タブで動画ファイルが正しく読み込まれているか確認

3. **Video IDを確認**
   - エージェントの応答に含まれる `videoId` を確認
   - `videomapping.json` に同じ ID が設定されているか確認

### 動画フォーマット

ブラウザで再生可能な形式：
- ? MP4 (H.264 + AAC) - 推奨
- ? WebM (VP8/VP9 + Vorbis/Opus)
- ? AVI, MOV（ブラウザで直接再生不可）

変換が必要な場合は、ffmpegなどのツールを使用してください：

```bash
ffmpeg -i input.mov -c:v libx264 -c:a aac output.mp4
```

## セキュリティに関する注意

本番環境では：
- 動画ファイルを Azure Blob Storage や CDN に配置
- `videomapping.json` に外部URLを設定
- アクセス制御を実装

例：
```json
{
  "VideoMapping": {
    "video1": {
      "title": "プロダクション動画",
      "file": "https://yourstorage.blob.core.windows.net/videos/video1.mp4"
    }
  }
}
```
