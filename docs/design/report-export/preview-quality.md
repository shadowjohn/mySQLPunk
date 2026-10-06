# Enlarged template previews

The preview window opens a separate high-resolution `SampleStem-preview.png`
instead of magnifying the small card thumbnail. It defaults to fit width, with
8 px image margins, scrolling, fit-preview / 100% / plus / minus controls and
Ctrl+mouse-wheel zoom. The window is resizable and stays within the active
monitor's working area. Escape closes it. The image is cloned so it does not
hold the bundled file open; closing the window disposes the clone.

The PNGs are excerpts of the bundled examples. The classic/delivery DOCX
samples receive the narrow header correction described below; other report
files and all card thumbnails are unchanged. Full examples remain available
through the existing Open sample action.

| Preview | Rendering source | Resolution |
| --- | --- | --- |
| docx-classic-preview.png | Word PDF, page 1, 180 dpi | 1348 × 1881 |
| docx-delivery-preview.png | Word PDF, page 2, 180 dpi | 1128 × 1662 |
| docx-schema-specification-preview.png | Word PDF, page 2, 180 dpi | 1257 × 689 |
| pdf-standard-preview.png | Bundled PDF, page 1, 180 dpi | 1375 × 1993 |
| pdf-compact-preview.png | Bundled PDF, page 1, 180 dpi | 1375 × 1993 |
| xlsx-tables-preview.png | Artifact Tool, customers sheet, scale 2 | 2460 × 736 |
| xlsx-inventory-preview.png | Artifact Tool, field_inventory sheet, scale 2 | 2902 × 1558 |

Word documents were opened read-only with macros disabled and closed without
saving. All three source SHA-256 values remained unchanged. Word/PDF renders
remove only outer empty page margins with 24 px retained around content;
interior spacing and all visible content remain intact. Spreadsheet previews
are marked as Artifact Tool previews, not Excel screenshots. No workbook was
exported or modified.

All seven PNGs were visually inspected. The native Word render exposed a
pre-existing narrow NULL header in the classic/delivery samples: its 624-twip
column left insufficient space after cell padding, so Word wrapped NUL / L.
Only that column was widened to 794 twips (1.4 cm), in both table grids and
cell widths. Other columns and document text remain unchanged; the classic
header retains vertical center and its 75-twip left padding. The same narrow
fix is reflected in the sample generator. Neither C# writer was changed.

The baseline Office 2007 Open XML validation also identified 114 missing
shading `val` attributes and 18 redundant newer table-look flags per sample.
The repaired files explicitly use shading `clear` and retain the equivalent
table-look `04A0` bitmask. Read-only SDK validation now reports zero errors
for both corrected DOCX samples. Byte comparisons verify that every other
ZIP member is unchanged and reverting only the targeted XML attributes
reconstructs the original document XML exactly.

Word was run read-only again after the correction: classic is now one page
and delivery remains two pages. All six NULL headers stay on one line. The
corrected enlarged images were inspected, and no content was clipped. The
specification preview remains an actual Word rendering, not an illustration.

Corrected source SHA-256 values (docs and application copies identical):

- classic: `60d165563b254f67cf0f4ca6220da77b2d3e106497330ca2797770ac13794e8b`
- delivery: `3873f5dc75ea0b4ee7eb0d5615261241afb17dbafdf461343aaaa6bae3b77ed6`

Reproduction scripts live in `tools/report-previews/`: first run
`render-word-previews.ps1` with a new output directory; then run
`render-large-pages.py` with the same QA directory and bundled Poppler;
`render-large-xlsx.mjs` independently reads the existing workbooks. Use the
bundled workspace Python/Node dependencies. PNGs are application assets;
intermediate PDFs and full-page renders stay under ignored implementation QA.

Boundary: asset rendering and visual inspection do not validate the current
C# report writers or native window interactions. No report application/test
executable was run to generate these previews.
