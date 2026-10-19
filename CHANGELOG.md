# Historial de versiones

## v0.8.0 — Herramientas de audio y vídeo
- Rueda de herramientas (Ctrl) para audio: recortar con forma de onda y escucha previa, quitar silencios al principio y al final, normalizar volumen (−16 LUFS), mono, estéreo y comprimir.
- Para vídeo: recortar con miniaturas y primer/último fotograma, comprimir a un tamaño objetivo (8, 16, 25, 50, 100 MB o el que elijas), quitar audio sin recomprimir y girar.

## v0.7.0 — Acciones, estante y teclado
- Rueda de acciones (Alt): copiar, mover, copiar ruta, terminal, compartir (panel Compartir de Windows) y estante.
- Estante flotante pegado al borde de la pantalla para dejar archivos y arrastrarlos después a otra app.
- Atajo de teclado (Ctrl+Alt+G) para abrir la rueda con los archivos seleccionados en el Explorador o el escritorio; se maneja con flechas, Intro, Tab y Esc.

## v0.6.0 — Herramientas de imagen, GPU y modo oscuro
- Tres ruedas: Mayús convierte, Ctrl abre herramientas y se puede cambiar de rueda con ella abierta.
- Herramientas de imagen: recortar (proporciones y tamaño), añadir fondo (degradados, margen, esquinas, sombra), comprimir, quitar metadatos y girar.
- PDF → JPG/PNG (una imagen por página, 300 ppp) y varias imágenes → un único PDF.
- Vídeo codificado con la tarjeta gráfica (NVIDIA NVENC, Intel Quick Sync, AMD AMF) tras detectarla y probarla; si falla, se repite con el procesador.
- La rueda y los avisos siguen el modo claro u oscuro de Windows.
- PDF → Word: número de página real en el encabezado, texto a dos columnas y OCR de imágenes que contienen texto.

## v0.5.0 — PDF → Word con maquetación
- Tablas reales de Word reconstruidas a partir de las líneas del PDF (celdas combinadas, bordes, rellenos), tablas anidadas.
- Tipografía, tamaño, negrita, color, subrayado, alineación, sangrías y espacios del original.
- Campos "______" como tabulaciones con relleno de subrayado; casillas con su fuente de símbolos.
- Encabezado y pie repetidos llevados al encabezado de Word; logos y dibujos como imágenes.

## v0.4.0 — PDF → Word con OCR
- Texto exacto de los PDF digitales (PdfPig) y OCR de Windows para páginas escaneadas.
- Reconstrucción de párrafos, títulos y saltos de página.

## v0.3.0 — Documentos
- TXT, Markdown, DOCX y HTML ↔ TXT, MD, DOCX, PDF, HTML, RTF y EPUB, sin librerías externas.

## v0.2.0 — Portable con motor integrado
- FFmpeg integrado dentro de `Gajo.exe` (no hay que descargar nada).
- Modo portable: ajustes junto al exe, sin registro salvo que se active el inicio con Windows.

## v0.1.0 — Primera versión
- Arrastrar + Mayús abre la rueda de conversión; imágenes, audio y vídeo con FFmpeg.
- Icono en la bandeja, avisos de progreso y ajustes.
