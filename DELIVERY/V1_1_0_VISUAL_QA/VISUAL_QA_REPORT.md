# V1.1.0 Synthetic Word Visual QA

These fixtures use synthetic Arabic/English content only. They are generated through `MeetingReportWordExporter.Export` with two synthetic PNG assets per report: one reading-edge-aligned content card and one full-width centered diagram. The exporter sets paragraph direction/alignment, inline image geometry, captions, RTL table direction, and section BiDi semantics.

Both DOCX files were opened read-only in Microsoft Word Print Layout. The capture manifest records two pages for each. Screenshots show both pages side by side at 55% Word zoom; Arabic paragraphs/table columns start at the right edge, English layout starts at the left edge, and figure captions align with each language's reading start. Images and chart bars fit their image frames after fixing the initial synthetic wide-chart geometry.

`OPENXML_VISUAL_PROOF.json` records section direction, image inline extents, paragraph/run BiDi, table BiDi, captions, and zero OpenXML validator errors. The Word screenshots are real desktop captures of the synthetic reports, not rendered substitutes.

| Evidence | File |
| --- | --- |
| Arabic report in Word, pages 1–2 | `WORD_AR_PAGE_1.png` |
| Arabic report in Word, continuation | `WORD_AR_PAGE_2.png` |
| English report in Word, pages 1–2 | `WORD_EN_PAGE_1.png` |
| English report in Word, continuation | `WORD_EN_PAGE_2.png` |
| Semantic and geometry proof | `OPENXML_VISUAL_PROOF.json` |
| Word capture metadata | `WORD_CAPTURE_MANIFEST.json` |
