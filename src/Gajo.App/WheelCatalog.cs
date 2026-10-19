using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gajo.Core;

namespace Gajo
{
    public enum WheelMode { Convert, Tools, Actions }

    /// <summary>One segment of a wheel: a format (Convert), a tool or an action.</summary>
    public sealed record WheelItem(string Id, string Label, string? Glyph = null, FormatTarget? Target = null);

    public sealed record WheelContent(WheelMode Mode, IReadOnlyList<WheelItem> Items, IReadOnlyList<string> Inputs, MediaKind Kind, string EmptyText);

    /// <summary>What each wheel offers for the dragged files.</summary>
    public static class WheelCatalog
    {
        // Segoe Fluent Icons / Segoe MDL2 Assets code points
        public const string GCrop = "", GRotate = "", GPhoto = "", GCompress = "", GInfo = "",
                            GCut = "", GVolume = "", GMute = "", GSpeaker = "", GVideo = "",
                            GCopy = "", GMove = "", GLink = "", GTerminal = "", GShare = "",
                            GPin = "", GTarget = "", GAudio = "", GSilence = "";

        /// <summary>Hides tools not available in this build.</summary>
        public static Func<string, bool> ToolFilter { get; set; } = _ => true;

        public static string ModeName(WheelMode m) => m switch
        {
            WheelMode.Tools => "Herramientas",
            WheelMode.Actions => "Acciones",
            _ => "Convertir",
        };

        public static WheelContent For(WheelMode mode, IReadOnlyList<string> paths)
        {
            switch (mode)
            {
                case WheelMode.Convert:
                {
                    var plan = Formats.PlanFor(paths);
                    var items = plan.Targets.Select(t => new WheelItem(t.Id, t.Label, null, t)).ToList();
                    return new WheelContent(mode, items, plan.Inputs, plan.Kind, "Sin conversiones");
                }
                case WheelMode.Tools:
                {
                    var plan = Formats.PlanFor(paths);
                    var items = ToolsFor(plan.Kind, plan.Inputs).Where(i => ToolFilter(i.Id)).ToList();
                    return new WheelContent(mode, items, plan.Inputs, plan.Kind, "Sin herramientas");
                }
                default:
                {
                    var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                    return new WheelContent(mode, Actions, existing, existing.Count > 0 ? Formats.KindOf(existing[0]) : MediaKind.Unknown, "Sin acciones");
                }
            }
        }

        private static List<WheelItem> ToolsFor(MediaKind kind, IReadOnlyList<string> inputs)
        {
            bool single = inputs.Count == 1;
            var list = new List<WheelItem>();
            switch (kind)
            {
                case MediaKind.Image:
                    if (single) list.Add(new("crop", "Recortar", GCrop));
                    if (single) list.Add(new("background", "Añadir fondo", GPhoto));
                    list.Add(new("compress", "Comprimir", GCompress));
                    list.Add(new("strip", "Quitar metadatos", GInfo));
                    list.Add(new("rotate", "Girar", GRotate));
                    break;
                case MediaKind.Audio:
                    if (single) list.Add(new("trim", "Recortar", GCut));
                    list.Add(new("silence", "Quitar silencios", GSilence));
                    list.Add(new("normalize", "Normalizar volumen", GVolume));
                    list.Add(new("mono", "Mono", GSpeaker));
                    list.Add(new("stereo", "Estéreo", GAudio));
                    list.Add(new("acompress", "Comprimir", GCompress));
                    break;
                case MediaKind.Video:
                    if (single) list.Add(new("trim", "Recortar", GCut));
                    list.Add(new("vcompress", "Comprimir a tamaño", GTarget));
                    list.Add(new("mute", "Quitar audio", GMute));
                    list.Add(new("vrotate", "Girar", GRotate));
                    break;
            }
            return list;
        }

        public static readonly IReadOnlyList<WheelItem> Actions = new[]
        {
            new WheelItem("copy", "Copiar", GCopy),
            new WheelItem("move", "Mover", GMove),
            new WheelItem("path", "Copiar ruta", GLink),
            new WheelItem("terminal", "Terminal", GTerminal),
            new WheelItem("share", "Compartir", GShare),
            new WheelItem("shelf", "Estante", GPin),
        };
    }
}
