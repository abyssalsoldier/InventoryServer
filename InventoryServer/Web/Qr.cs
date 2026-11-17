using QRCoder;

namespace InventoryServer.Web;

public static class Qr
{
    public static string UrlFor(ServerProfile p, string sku) => $"{p.PublicUrl}/i/{Uri.EscapeDataString(sku)}";

    public static byte[] Png(string text, int pixelsPerModule = 10)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
