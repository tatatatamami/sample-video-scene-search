# 動画マッピング設定ガイド

## 概要

このアプリケーションでは、Azure AI Foundry Agent から返される `videoId` を実際の動画 URL にマッピングする必要があります。この設定は `Pages/Index.cshtml` 内の JavaScript で行います。

## 設定場所

ファイル: `VideoSceneSearch/Pages/Index.cshtml`

JavaScript セクション内の `videoMapping` オブジェクト：

```javascript
const videoMapping = {
    'video1': 'URL_TO_VIDEO1',
    'video2': 'URL_TO_VIDEO2',
    // ... more mappings
};
```

## 設定例

### 1. Azure Blob Storage を使用する場合

#### パブリックアクセス（推奨しない）

```javascript
const videoMapping = {
    'meeting-2024-01': 'https://mystorageaccount.blob.core.windows.net/videos/meeting-2024-01.mp4',
    'product-demo': 'https://mystorageaccount.blob.core.windows.net/videos/product-demo.mp4',
};
```

#### SAS トークンを使用（推奨）

```javascript
const videoMapping = {
    'meeting-2024-01': 'https://mystorageaccount.blob.core.windows.net/videos/meeting-2024-01.mp4?sp=r&st=2024-02-01T00:00:00Z&se=2024-12-31T23:59:59Z&spr=https&sv=2022-11-02&sr=b&sig=...',
    'product-demo': 'https://mystorageaccount.blob.core.windows.net/videos/product-demo.mp4?sp=r&st=2024-02-01T00:00:00Z&se=2024-12-31T23:59:59Z&spr=https&sv=2022-11-02&sr=b&sig=...',
};
```

**SAS トークンの生成方法：**

```bash
# Azure CLI を使用
az storage blob generate-sas \
    --account-name mystorageaccount \
    --container-name videos \
    --name meeting-2024-01.mp4 \
    --permissions r \
    --expiry 2024-12-31T23:59:59Z \
    --https-only
```

### 2. Azure Media Services を使用する場合

```javascript
const videoMapping = {
    'meeting-2024-01': 'https://myams-uswe.streaming.media.azure.net/abc123/meeting-2024-01.ism/manifest(format=m3u8-aapl)',
    'product-demo': 'https://myams-uswe.streaming.media.azure.net/def456/product-demo.ism/manifest(format=m3u8-aapl)',
};
```

### 3. CDN を使用する場合

```javascript
const videoMapping = {
    'meeting-2024-01': 'https://mycdn.azureedge.net/videos/meeting-2024-01.mp4',
    'product-demo': 'https://mycdn.azureedge.net/videos/product-demo.mp4',
};
```

### 4. 複数のストレージアカウント

```javascript
const videoMapping = {
    // ストレージアカウント 1
    'training-video-1': 'https://storage1.blob.core.windows.net/training/video1.mp4',
    'training-video-2': 'https://storage1.blob.core.windows.net/training/video2.mp4',
    
    // ストレージアカウント 2
    'marketing-video-1': 'https://storage2.blob.core.windows.net/marketing/video1.mp4',
    'marketing-video-2': 'https://storage2.blob.core.windows.net/marketing/video2.mp4',
};
```

### 5. テスト用（パブリックサンプル動画）

開発・テスト用のサンプルマッピング：

```javascript
const videoMapping = {
    'sample1': 'https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4',
    'sample2': 'https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4',
    'sample3': 'https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ForBiggerBlazes.mp4',
    'sample4': 'https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/Sintel.mp4',
};
```

## 動的な URL 生成（高度な設定）

### SAS トークンをバックエンドで生成する場合

より安全な方法として、バックエンドで SAS トークンを生成することができます：

1. **新しい API エンドポイントを追加** (`Program.cs`):

```csharp
app.MapGet("/api/video-url/{videoId}", (string videoId) =>
{
    // Azure.Storage.Blobs を使用して SAS トークンを生成
    var blobServiceClient = new BlobServiceClient(connectionString);
    var containerClient = blobServiceClient.GetBlobContainerClient("videos");
    var blobClient = containerClient.GetBlobClient($"{videoId}.mp4");
    
    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = "videos",
        BlobName = $"{videoId}.mp4",
        Resource = "b",
        StartsOn = DateTimeOffset.UtcNow,
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
    };
    sasBuilder.SetPermissions(BlobSasPermissions.Read);
    
    var sasToken = blobClient.GenerateSasUri(sasBuilder);
    return Results.Ok(new { url = sasToken.ToString() });
});
```

2. **フロントエンドを更新** (`Index.cshtml`):

```javascript
async function playVideo(videoId, startSec, title, description) {
    try {
        // バックエンドから URL を取得
        const response = await fetch(`/api/video-url/${videoId}`);
        const data = await response.json();
        const videoUrl = data.url;
        
        videoPlayer.src = videoUrl;
        videoTitle.textContent = title;
        videoDescription.textContent = description;
        videoSection.style.display = 'block';
        
        videoPlayer.addEventListener('loadedmetadata', () => {
            videoPlayer.currentTime = startSec;
            videoPlayer.play();
        }, { once: true });
        
        videoSection.scrollIntoView({ behavior: 'smooth' });
    } catch (error) {
        alert(`動画の読み込みに失敗しました: ${error.message}`);
    }
}
```

## Azure AI Foundry Agent の設定

Agent が返す JSON に含まれる `videoId` は、この設定と一致させる必要があります。

### Agent プロンプト例

```
あなたは動画検索アシスタントです。
ユーザーのクエリに基づいて、以下の形式でJSON配列を返してください：

[
  {
    "videoId": "meeting-2024-01",
    "title": "会議の録画",
    "startSec": 120.5,
    "endSec": 180.0,
    "description": "製品ロードマップに関する議論",
    "score": 0.95
  }
]

利用可能な動画ID：
- meeting-2024-01
- meeting-2024-02
- product-demo
- training-video-1
```

## トラブルシューティング

### 動画が再生されない

1. **videoId が見つからない**
   - ブラウザのコンソールに `動画ID "xxx" の URL が見つかりません` と表示される
   - `videoMapping` に該当する `videoId` を追加してください

2. **CORS エラー**
   - Azure Blob Storage の CORS 設定を確認：
   
   ```bash
   az storage cors add \
       --methods GET OPTIONS \
       --origins '*' \
       --allowed-headers '*' \
       --exposed-headers '*' \
       --max-age 86400 \
       --services b \
       --account-name mystorageaccount
   ```

3. **SAS トークンの有効期限切れ**
   - SAS トークンの有効期限を確認し、新しいトークンを生成してください

4. **動画形式がサポートされていない**
   - HTML5 video タグがサポートする形式（MP4/H.264 推奨）を使用してください

### デバッグ方法

ブラウザの開発者ツール（F12）を開き、以下を確認：

1. **コンソールタブ**: エラーメッセージを確認
2. **ネットワークタブ**: 動画ファイルのリクエストステータスを確認
3. **コンソールで videoMapping を確認**:
   ```javascript
   console.log(videoMapping);
   ```

## セキュリティのベストプラクティス

1. **SAS トークンを使用**
   - 限定的なアクセス許可（読み取りのみ）
   - 短い有効期限（数時間〜1日）
   - HTTPS のみ

2. **動的 SAS トークン生成**
   - バックエンドで生成し、必要な時にのみフロントエンドに提供

3. **アクセス制御**
   - ユーザー認証・認可を実装
   - 適切なユーザーのみが特定の動画にアクセスできるようにする

4. **ログ記録**
   - 動画アクセスをログに記録
   - 異常なアクセスパターンを監視

## パフォーマンス最適化

1. **CDN の使用**
   - Azure CDN で動画を配信
   - グローバルなユーザーに低レイテンシで配信

2. **アダプティブストリーミング**
   - Azure Media Services で HLS/DASH を使用
   - ネットワーク状況に応じて品質を自動調整

3. **動画の最適化**
   - 適切なビットレートとコーデック（H.264/H.265）
   - 複数の解像度を用意（480p, 720p, 1080p）

4. **プリロード設定**
   ```html
   <video preload="metadata">
   ```
   - `metadata`: メタデータのみプリロード（デフォルト）
   - `auto`: 動画全体をプリロード
   - `none`: プリロードしない
