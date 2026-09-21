using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using WinRT;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SheetMusicViewer;

/// <summary>Renders PDF pages through the Windows 11 PDF platform API.</summary>
public sealed class PdfRenderer : IDisposable
{
    // These limits bound allocations made while handling local, potentially
    // untrusted documents. A rendered pixel can require at least four bytes
    // before the PNG and WPF decoding buffers are considered.
    public const ulong MaximumDocumentSizeBytes = 100UL * 1024 * 1024;
    public const int MaximumDocumentPages = 50;
    public const int MaximumRenderDimension = 4096;
    public const long MaximumRenderPixels = 12_000_000;

    private PdfDocument? _document;
    private InMemoryRandomAccessStream? _documentStream;
    public bool IsOpen => _document is not null;
    public int PageCount => (int)(_document?.PageCount ?? 0);

    public async Task OpenAsync(string path)
    {
        DisposeDocument();
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var fileStream = await file.OpenReadAsync();
        if (fileStream.Size > MaximumDocumentSizeBytes)
        {
            throw new PdfResourceLimitException(
                $"Die PDF ist zu groß. Unterstützt werden höchstens {MaximumDocumentSizeBytes / 1024 / 1024} MiB.");
        }

        var documentStream = new InMemoryRandomAccessStream();
        try
        {
            await RandomAccessStream.CopyAsync(fileStream, documentStream);
            documentStream.Seek(0);
            var document = await PdfDocument.LoadFromStreamAsync(documentStream);
            if (document.PageCount > (uint)MaximumDocumentPages)
            {
                DisposeDocument(document);
                throw new PdfResourceLimitException(
                    $"Die PDF hat {document.PageCount} Seiten. Unterstützt werden höchstens {MaximumDocumentPages} Seiten.");
            }

            _document = document;
            _documentStream = documentStream;
        }
        catch
        {
            documentStream.Dispose();
            throw;
        }
    }

    public async Task<BitmapSource?> RenderPageAsync(int pageIndex, int maxWidth, int maxHeight, CancellationToken cancellationToken)
    {
        var document = _document ?? throw new InvalidOperationException("Keine PDF geöffnet.");
        if (pageIndex < 0 || (uint)pageIndex >= document.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        ValidateRenderBounds(maxWidth, maxHeight);
        using var page = document.GetPage((uint)pageIndex);
        if (!double.IsFinite(page.Size.Width) || !double.IsFinite(page.Size.Height) || page.Size.Width <= 0 || page.Size.Height <= 0)
        {
            throw new PdfResourceLimitException("Die PDF enthält eine Seite mit ungültigen Abmessungen.");
        }

        var scale = Math.Min(maxWidth / page.Size.Width, maxHeight / page.Size.Height);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new PdfResourceLimitException("Die PDF-Seite kann nicht sicher gerendert werden.");
        }

        var destinationWidth = Math.Max(1, Math.Round(page.Size.Width * scale));
        var destinationHeight = Math.Max(1, Math.Round(page.Size.Height * scale));
        if (destinationWidth > MaximumRenderDimension || destinationHeight > MaximumRenderDimension ||
            destinationWidth * destinationHeight > MaximumRenderPixels)
        {
            throw new PdfResourceLimitException("Die angeforderte Seitendarstellung ist zu groß und wurde zum Schutz des Speichers abgebrochen.");
        }

        var options = new PdfPageRenderOptions
        {
            BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            DestinationWidth = (uint)destinationWidth,
            DestinationHeight = (uint)destinationHeight
        };
        using var stream = new InMemoryRandomAccessStream();
        cancellationToken.ThrowIfCancellationRequested();
        await page.RenderToStreamAsync(stream, options);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        using var input = stream.AsStreamForRead();
        using var copy = new MemoryStream();
        await input.CopyToAsync(copy, cancellationToken);
        copy.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = copy;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void ValidateRenderBounds(int maxWidth, int maxHeight)
    {
        if (maxWidth is < 1 or > MaximumRenderDimension || maxHeight is < 1 or > MaximumRenderDimension ||
            (long)maxWidth * maxHeight > MaximumRenderPixels)
        {
            throw new PdfResourceLimitException(
                $"Die angeforderte Darstellung überschreitet das Limit von {MaximumRenderDimension} Pixeln je Kante und {MaximumRenderPixels:N0} Pixeln.");
        }
    }

    private void DisposeDocument()
    {
        DisposeDocument(_document);
        _document = null;
        _documentStream?.Dispose();
        _documentStream = null;
    }

    private static void DisposeDocument(PdfDocument? document)
    {
        // PdfDocument is a WinRT IClosable object.  Releasing the managed reference
        // alone leaves its native file handle open until the GC eventually runs.
        if (document is IWinRTObject winRtDocument)
        {
            winRtDocument.NativeObject.Dispose();
        }
    }

    public void Close() => DisposeDocument();

    public void Dispose() => DisposeDocument();
}

public sealed class PdfResourceLimitException(string message) : InvalidOperationException(message);
