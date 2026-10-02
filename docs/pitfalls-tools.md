# Грабли: MCP, редактор Unity, правка файлов

Читать перед работой через MCP, отладкой в Play и массовой правкой файлов.

## MCP
- `.mcp.json` → `http://127.0.0.1:8767/mcp`. Car_Train занимает 8765 — порт хранится в EditorPrefs на всю машину,
  поэтому `McpPortPin.cs` перезаписывает его при загрузке редактора.
- Проверка: `curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8767/mcp` → 406 = сервер жив.
  Если сервер жив, а сессия пишет ECONNREFUSED — переподключить через /mcp (сессия стартовала раньше редактора).
- **Скрин `manage_camera screenshot`** пишет только внутрь проекта: `output_folder: "Temp/Shots"`.
- **Компиляция**: `refresh_unity` compile=request, mode=force, scope=all; бывает «idle» без сборки — проверять появление
  нового типа/поля через `execute_code`, при необходимости повторить.
- **`execute_code` без `action:"execute"` падает**; codedom (C# 6, без `dynamic`) — без `StringBuilder.Append` цепочкой,
  собирать строку через `+`.
- `read_console types` — списком.

## Отладка в Play
- `execute_code` → `Kare.Space.EditorTools.FlightDebug` через рефлексию (`Reentry(alt, speed, γ°)`, `Stage`, `Status`,
  `Lift(alt, up)`); ускорение — `Time.timeScale`; поля `FlightCamera.Yaw/Pitch/Distance` публичные (Yaw от севера).
- **Телепорт борта**: сначала `v.Situation = Flying` (иначе `UpdateLandedPose` вернёт на стол), и пауза
  `manage_editor pause` — переключатель: второй вызов снимает паузу.

## Правка файлов (Windows, Git Bash)
- Длинный Python в heredoc Bash падает — писать скрипт в файл (скретчпад) и запускать `python файл`.
- Замены через Python: `io.open(..., newline='')`, учитывать CRLF, assert на число вхождений.
- `perl -i` на файлах с кириллицей не использовать — портит кодировку.
