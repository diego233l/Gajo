using System.Threading;
using System.Threading.Tasks;

namespace Gajo.Core
{
    /// <summary>
    /// Microsoft Office, when it is installed, gives the best Word/Excel/PowerPoint → PDF results.
    /// On Windows this is implemented with Office's own automation (invisible, no windows).
    /// Without Office, Gajo uses its own simpler converters.
    /// </summary>
    public interface IOfficeConverter
    {
        bool HasWord { get; }
        bool HasExcel { get; }
        bool HasPowerPoint { get; }

        /// <summary>format: "pdf", "docx", "xlsx" or "pptx".</summary>
        Task SaveAsAsync(string input, string output, string format, CancellationToken ct);

        /// <summary>One image per slide (ext "png" or "jpg") into <paramref name="folder"/>.</summary>
        Task ExportSlidesAsync(string input, string folder, string ext, CancellationToken ct);
    }

    public static class Office
    {
        public static IOfficeConverter? Converter { get; set; }
        public static bool Word => Converter?.HasWord == true;
        public static bool Excel => Converter?.HasExcel == true;
        public static bool PowerPoint => Converter?.HasPowerPoint == true;
    }
}
