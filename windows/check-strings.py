"""Каждая строка из L10n.T("…") должна иметь русский перевод.

Строка без ключа не ломает приложение — она просто показывается по-английски
тому, у кого Windows на русском. Поэтому проверка отдельная и быстрая: CI
запускает её до сборки. Запуск: python3 windows/check-strings.py
"""
import pathlib, re, sys

root = pathlib.Path(__file__).parent
call = re.compile(r'L10n\.T\(\s*"((?:[^"\\]|\\.)*)"')
# Ключи, которые попадают в L10n.T не литералом, а через переменную.
indirect = {"Music", "Shelf", "Clipboard", "Snippets", "Calendar", "Translate", "Currency",
            "Notes", "Teleprompter", "Utilities", "Settings", "Russian", "English",
            "B", "KB", "MB", "GB"}

used = set(indirect)
for path in list(root.glob("Cyclop/**/*.cs")) + list(root.glob("Cyclop.Core/**/*.cs")):
    if "/obj/" in path.as_posix() or "/bin/" in path.as_posix():
        continue
    used.update(m.group(1) for m in call.finditer(path.read_text(encoding="utf-8")))

table = (root / "Cyclop.Core/Strings.ru.strings").read_text(encoding="utf-8")
have = set(re.findall(r'^\s*"((?:[^"\\]|\\.)*)"\s*=', table, re.M))

missing = sorted(used - have)
for key in missing:
    print(f"нет перевода: {key!r}")
sys.exit(1 if missing else 0)
