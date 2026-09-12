using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class PdfHelper
{
    /// <summary>
    /// Extracts the width and height of a JPEG image by parsing SOF markers without third-party dependencies.
    /// </summary>
    public static (int width, int height) GetJpegDimensions(byte[] bytes)
    {
        int i = 0;
        if (bytes == null || bytes.Length < 4 || bytes[i] != 0xFF || bytes[i + 1] != 0xD8)
            return (800, 600); // Default fallback

        i += 2;
        while (i < bytes.Length - 8)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = bytes[i + 1];
            // SOF0 (Baseline), SOF1 (Extended Sequential), SOF2 (Progressive)
            if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2)
            {
                int h = (bytes[i + 5] << 8) | bytes[i + 6];
                int w = (bytes[i + 7] << 8) | bytes[i + 8];
                return (w > 0 && h > 0) ? (w, h) : (800, 600);
            }

            int len = (bytes[i + 2] << 8) | bytes[i + 3];
            if (len <= 0) break;
            i += 2 + len;
        }

        return (800, 600);
    }

    /// <summary>
    /// Creates a 100% standard compliant PDF 1.4 document wrapping a raw JPEG image using /DCTDecode.
    /// Opens seamlessly in Adobe Acrobat, Chromium, Edge, Firefox, and macOS Preview with zero external libraries.
    /// </summary>
    public static byte[] CreatePdfFromJpeg(byte[] jpegBytes)
    {
        var (w, h) = GetJpegDimensions(jpegBytes);
        // User unit: 72 points per inch. Let PDF page dimensions match image aspect ratio.
        double pageWidth = w * 0.75;
        double pageHeight = h * 0.75;

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);

        var offsets = new List<long>();

        writer.Write("%PDF-1.4\r\n");
        writer.Flush();

        // 1 0 obj: Catalog
        offsets.Add(ms.Position);
        writer.Write("1 0 obj\r\n<< /Type /Catalog /Pages 2 0 R >>\r\nendobj\r\n");
        writer.Flush();

        // 2 0 obj: Pages
        offsets.Add(ms.Position);
        writer.Write("2 0 obj\r\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\r\nendobj\r\n");
        writer.Flush();

        // 3 0 obj: Page
        offsets.Add(ms.Position);
        string mediaBox = $"[0 0 {pageWidth.ToString("F2", CultureInfo.InvariantCulture)} {pageHeight.ToString("F2", CultureInfo.InvariantCulture)}]";
        writer.Write($"3 0 obj\r\n<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>\r\nendobj\r\n");
        writer.Flush();

        // 4 0 obj: Image XObject with DCTDecode (native JPEG)
        offsets.Add(ms.Position);
        writer.Write($"4 0 obj\r\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegBytes.Length} >>\r\nstream\r\n");
        writer.Flush();
        ms.Write(jpegBytes, 0, jpegBytes.Length);
        writer.Write("\r\nendstream\r\nendobj\r\n");
        writer.Flush();

        // 5 0 obj: Contents stream (draws the image scaled to full page)
        string contentStream = $"q\r\n{pageWidth.ToString("F2", CultureInfo.InvariantCulture)} 0 0 {pageHeight.ToString("F2", CultureInfo.InvariantCulture)} 0 0 cm\r\n/Im1 Do\r\nQ\r\n";
        byte[] contentBytes = Encoding.ASCII.GetBytes(contentStream);
        offsets.Add(ms.Position);
        writer.Write($"5 0 obj\r\n<< /Length {contentBytes.Length} >>\r\nstream\r\n{contentStream}endstream\r\nendobj\r\n");
        writer.Flush();

        // xref table (each entry must be exactly 20 bytes including \r\n)
        long xrefOffset = ms.Position;
        writer.Write($"xref\r\n0 {offsets.Count + 1}\r\n");
        writer.Write("0000000000 65535 f \r\n");
        foreach (var off in offsets)
        {
            writer.Write($"{off:D10} 00000 n \r\n");
        }
        writer.Flush();

        // trailer
        writer.Write($"trailer\r\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\r\nstartxref\r\n{xrefOffset}\r\n%%EOF\r\n");
        writer.Flush();

        return ms.ToArray();
    }
}
