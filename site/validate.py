from html.parser import HTMLParser
from pathlib import Path
import subprocess
import tempfile

page = Path("site/index.html").read_text(encoding="utf-8")

class Audit(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = set()
        self.duplicates = []
        self.anchors = []
        self.inline_scripts = []
        self.in_script = False
        self.script_parts = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        element_id = attrs.get("id")
        if element_id:
            if element_id in self.ids:
                self.duplicates.append(element_id)
            self.ids.add(element_id)
        href = attrs.get("href", "")
        if href.startswith("#"):
            self.anchors.append(href[1:])
        if tag == "script" and not attrs.get("src"):
            self.in_script = True
            self.script_parts = []

    def handle_data(self, data):
        if self.in_script:
            self.script_parts.append(data)

    def handle_endtag(self, tag):
        if tag == "script" and self.in_script:
            self.inline_scripts.append("".join(self.script_parts))
            self.in_script = False

audit = Audit()
audit.feed(page)
assert not audit.duplicates, f"Duplicate HTML ids: {audit.duplicates}"
missing = sorted(set(audit.anchors) - audit.ids)
assert not missing, f"Broken in-page links: {missing}"
for required in ("architecture", "lifecycle", "proof", "evidence", "approvals", "boundaries"):
    assert required in audit.ids, f"Missing required section: {required}"
assert "documented Execution–CiAi contract, not a live integration" in page
assert "No live dispatch" in page
assert audit.inline_scripts, "No inline interaction script found"

with tempfile.NamedTemporaryFile("w", suffix=".js", encoding="utf-8") as script:
    script.write("".join(audit.inline_scripts))
    script.flush()
    subprocess.run(["node", "--check", script.name], check=True)

print(f"PASS: {len(audit.ids)} unique ids; {len(audit.anchors)} in-page links; {len(audit.inline_scripts)} inline script(s) parse.")
