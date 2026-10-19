# Gajo

[![Compilar Gajo](https://github.com/diego233l/Gajo/actions/workflows/build.yml/badge.svg)](https://github.com/diego233l/Gajo/actions/workflows/build.yml)
[![Última versión](https://img.shields.io/github/v/release/diego233l/Gajo?label=descargar)](https://github.com/diego233l/Gajo/releases/latest)

Conversor de archivos portable para Windows, al estilo de Tangerine (macOS): **arrastra un archivo, pulsa Mayús sin soltarlo y suéltalo sobre el formato que quieras**. El archivo nuevo aparece junto al original y el original no se toca. Todo funciona en tu PC, sin internet.

## Descarga

Descarga `Gajo-vX.Y-win-x64.zip` desde [Releases](https://github.com/diego233l/Gajo/releases/latest), descomprímelo y abre `Gajo.exe`. Necesitas Windows 10 o 11 de 64 bits.

Los cambios de cada versión están en [CHANGELOG.md](CHANGELOG.md).

## Uso (portable)

`Gajo.exe` es un único archivo que lo lleva todo dentro, incluido el motor de conversión (FFmpeg). No se instala nada.

1. Copia `Gajo.exe` a cualquier carpeta y ábrelo. Como no está firmado, Windows puede avisar con "Windows protegió su PC": pulsa *Más información* → *Ejecutar de todas formas*.
2. Gajo queda en segundo plano, en los **iconos ocultos** de la barra de tareas (la flecha ^ junto al reloj). Si quieres verlo siempre, arrastra el icono a la barra.
3. Arrastra uno o varios archivos y, **sin soltar el ratón**, pulsa y suelta **Mayús**. Aparece la rueda: suelta el archivo sobre un formato.
4. Para cancelar, suelta en el centro o fuera de la rueda, o pulsa **Esc**.

**Ajustes:** doble clic en el icono de la bandeja. **Salir:** clic derecho → *Salir*.

### Qué deja en tu PC

- `Gajo.settings.json`, junto a `Gajo.exe`, solo si cambias algún ajuste.
- `%TEMP%\Gajo\`: el motor desempaquetado (unos 105 MB) y el registro de errores. Es una carpeta temporal: puedes borrarla cuando quieras y Gajo la vuelve a crear. Al actualizar Gajo, la copia antigua se borra sola.
- `%TEMP%\.net\Gajo\`: piezas de .NET que se extraen automáticamente al arrancar.
- Nada en el registro, salvo que actives *Iniciar con Windows*. Si la activas, desmárcala antes de borrar Gajo.

Para "desinstalar", cierra Gajo y borra `Gajo.exe`. Para actualizarlo, ciérralo y sustituye el archivo.

## Las ruedas

| Tecla (mientras arrastras) | Rueda | Qué hace |
|---|---|---|
| **Mayús** | Convertir | Cambia el formato (JPG → PNG, PDF → Word, vídeo → MP4…). |
| **Ctrl** | Herramientas | Imagen: recortar, añadir fondo, comprimir, quitar metadatos, girar. Audio: recortar, quitar silencios, normalizar volumen, mono, estéreo, comprimir. Vídeo: recortar, comprimir a un tamaño, quitar audio, girar. |
| **Alt** | Acciones | Copiar, mover, copiar ruta, abrir terminal, compartir y estante (sirven para cualquier archivo o carpeta). |

### Acciones (Alt)

- **Copiar**: deja los archivos en el portapapeles, listos para pegarlos con Ctrl+V.
- **Mover**: elige una carpeta y mueve allí los originales.
- **Copiar ruta**: copia la ruta completa, una por línea.
- **Terminal**: abre Windows Terminal (o PowerShell) en esa carpeta.
- **Compartir**: abre el panel Compartir de Windows (Uso compartido en proximidad, correo, apps…), el equivalente a AirDrop.
- **Estante**: un panel flotante pegado al borde de la pantalla donde dejas archivos un momento para arrastrarlos después a otra app. También puedes soltar archivos directamente en él. Ábrelo desde el icono de la bandeja → *Mostrar estante*.

### Con el teclado

Selecciona archivos en el Explorador o el escritorio y pulsa **Ctrl+Alt+G** (se puede cambiar en Ajustes). Se abre la rueda: **flechas** para elegir, **Intro** para aplicar, **Tab** (o Mayús/Ctrl/Alt) para cambiar de rueda y **Esc** para cerrar. También puedes hacer clic en una opción.

Con la rueda abierta puedes pulsar la otra tecla para cambiar de rueda. Las teclas se pueden cambiar en Ajustes. La rueda sigue el modo claro u oscuro de Windows.

**Recortar** abre una ventana con el marco y sus asas, proporciones (libre, 1:1, 16:9, 9:16, 4:3, 3:4) y tamaño en píxeles. **Añadir fondo** coloca la imagen sobre un degradado o color, con margen, esquinas redondeadas, sombra y proporción (automática, 1:1, 16:9, 4:5, 9:16), y la guarda como PNG.

**Recortar audio o vídeo** muestra la forma de onda (y miniaturas en los vídeos) con dos asas para el inicio y el final. Puedes escribir los tiempos, escuchar la selección en el audio y ver el primer y el último fotograma en el vídeo. **Quitar silencios** elimina solo los silencios del principio y del final, no las pausas. **Normalizar** iguala el volumen al estándar de podcasts y streaming (−16 LUFS). **Comprimir a tamaño** genera un MP4 que no pasa del tamaño elegido (8 MB para Discord, 16 MB para WhatsApp, 25 MB para correo, 50, 100 o el que escribas), y baja la resolución si hace falta.

## Tarjeta gráfica

Al arrancar, Gajo mira qué tarjetas gráficas tiene el PC y prueba de verdad si pueden codificar vídeo (NVIDIA NVENC, Intel Quick Sync o AMD AMF). Solo las usa donde aceleran, es decir, al crear vídeo H.264 (MP4, MOV, MKV). Si con un vídeo concreto la tarjeta falla, Gajo lo repite con el procesador sin que tengas que hacer nada. En Ajustes verás la tarjeta detectada y puedes desactivarlo.

## Formatos (v0.8)

| Tipo | Entrada | Rueda de salida |
|---|---|---|
| Imagen | JPG, PNG, WebP, HEIC, AVIF, TIFF, BMP, ICO, TGA | JPG, PNG, WEBP, AVIF, TIFF, BMP, ICO, PDF |
| Audio | MP3, M4A, AAC, WAV, FLAC, OGG, Opus, AIFF, WMA, AMR | MP3, M4A, WAV, FLAC, OGG, OPUS, AIFF, WMA |
| Vídeo | MP4, MOV, MKV, WebM, AVI, WMV, GIF, MPG, FLV, 3GP, TS | MP4, MOV, MKV, WEBM, AVI, WMV, GIF, MP3 (extrae el audio) |
| Documento | TXT, Markdown, DOCX, HTML, **PDF** | TXT, MD, DOCX, PDF, HTML, RTF, EPUB; desde PDF también **JPG/PNG** (una imagen por página a 300 ppp) |

### PDF → Word (con maquetación y OCR)

- **Tablas reales de Word** reconstruidas a partir de las líneas del PDF: celdas combinadas, bordes, grosores y rellenos grises. Las tablas anidadas se integran en la tabla que las contiene.
- **Formato del texto**: tipografía, tamaño, negrita, cursiva, color, subrayado, alineación (izquierda, centrado, derecha, justificado), sangrías y espacio vertical.
- **Campos de formulario**: las rayas "______" pasan a ser tabulaciones con relleno de subrayado que terminan donde en el PDF, y los huecos grandes de una línea pasan a ser tabulaciones en la posición exacta.
- **Casillas y símbolos** (Wingdings, Symbol) con la misma fuente de Windows.
- **Número de página real**: si el PDF numera sus páginas en la cabecera (30709, 30710…), el Word usa un campo de número de página que empieza en el mismo número.
- **Texto a dos columnas**: se detecta y se convierte en columnas de Word por las que el texto fluye, y los títulos que ocupan todo el ancho se quedan fuera de las columnas.
- **Imágenes con texto**: las imágenes que son texto (un párrafo pegado como captura, por ejemplo) se convierten en texto editable con el OCR de Windows. En las fotos que solo tienen algo de texto, ese texto se guarda como descripción de la imagen.
- **Encabezado y pie repetidos** (logo, fecha del boletín…): pasan al encabezado o pie de Word como imagen, sin los números de página.
- **Logos y dibujos** como imágenes, renderizados con el motor PDF de Windows.
- **Páginas escaneadas**: se leen con el OCR de Windows.
- Solo hay saltos de página donde el PDF dejaba la página medio vacía. Así, si algo ocupa un poco más en Word, no quedan páginas casi en blanco.

Si sueltas **varias imágenes** sobre PDF, se crea un único PDF con una página por imagen.

La rueda oculta el formato que ya tiene el archivo. Si un nombre ya existe, se crea `nombre 2.ext`.

## Compilar

Hace falta el SDK de .NET 8 (`winget install Microsoft.DotNet.SDK.8`). Después, doble clic en **`compilar.bat`**. La primera compilación descarga FFmpeg 9.0.2 (compilación *essentials* de Gyan) en `third_party/` y lo integra comprimido en `publish\Gajo.exe`. Las siguientes reutilizan esa copia.

También puedes subir la carpeta a GitHub: el workflow `.github/workflows/build.yml` compila `Gajo.exe` y lo deja en *Actions* → *Artifacts*.

Para compilar sin motor integrado (el exe pesa unos 65 MB y usa un `ffmpeg.exe` que tú elijas en Ajustes), añade `-p:EmbedFfmpeg=false`.

## Cómo funciona por dentro

- `Interop/GlobalHooks.cs`: ganchos globales de ratón y teclado. Solo observan, no bloquean nada.
- `DragWatcher.cs`: detecta "botón izquierdo pulsado + movimiento + tecla de activación".
- `UI/WheelWindow.cs`: la rueda. Es una ventana sin bordes que no roba el foco y actúa como destino de arrastre. Aparece casi invisible bajo el cursor, Windows le entrega la lista de archivos y entonces se muestra. Siempre responde "copiar", así que el Explorador nunca mueve ni borra el original.
- `Gajo.Core/PdfLayout.cs` + `PdfToDocx.cs`: análisis de la maquetación (líneas → tablas, estilos, figuras, cabeceras) y escritura del DOCX. `WindowsPdfRenderer.cs`: recortes de página con `Windows.Data.Pdf`.
- `Gajo.Core/PdfText.cs`: texto de PDF con PdfPig (Apache 2.0, integrado) y reconstrucción de párrafos y títulos. `WindowsOcr.cs`: OCR con `Windows.Media.Ocr` y renderizado con `Windows.Data.Pdf`, ambos de Windows.
- `Gajo.Core/EmbeddedPayload.cs`: guarda FFmpeg comprimido con Brotli al final de `Gajo.exe` y lo desempaqueta una vez en `%TEMP%\Gajo`.
- `tools/Gajo.Pack`: el paso de compilación que hace esa integración.
- `tests/Gajo.Smoke`: convierte muestras a todos los formatos (`dotnet run --project tests/Gajo.Smoke`).

## Limitaciones conocidas

- Si arrastras desde un programa ejecutado como **administrador**, Windows no permite soltar en Gajo, igual que con cualquier otra app.
- Los archivos "virtuales" (dentro de un .zip abierto en el Explorador, de un móvil o arrastrados desde el navegador) no tienen ruta real y no se pueden convertir.
- Todavía no están: la rueda de herramientas (Ctrl: comprimir, recortar, añadir fondo…), la de acciones ni las conversiones desde hojas de cálculo, presentaciones o EPUB. En documentos se conserva el texto, los títulos y las listas, pero no las imágenes, tablas ni formatos como negrita.

## Licencia de FFmpeg

Gajo se publica bajo la licencia **GPL v3** (ver [LICENSE](LICENSE)), la misma de FFmpeg, que va incluido en `Gajo.exe`. Usa además [PdfPig](https://github.com/UglyToad/PdfPig) (Apache 2.0).
