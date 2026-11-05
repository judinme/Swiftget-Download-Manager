"""
SwiftGet Documentation PDF Compiler
Compiles docs/SwiftGet_Full_Documentation.html to PDF via headless Google Chrome.
"""

import subprocess
import sys
import shutil
from pathlib import Path

WORKSPACE_ROOT = Path("d:/SwiftGet")
INPUT_HTML = WORKSPACE_ROOT / "docs" / "SwiftGet_Full_Documentation.html"
OUTPUT_PDF = WORKSPACE_ROOT / "docs" / "SwiftGet_Full_Documentation.pdf"
DIST_PDF = WORKSPACE_ROOT / "dist" / "SwiftGet_Full_Documentation.pdf"
CHROME_PATH = Path("C:/Program Files/Google/Chrome/Application/chrome.exe")

def main():
    if not INPUT_HTML.exists():
        print(f"Error: {INPUT_HTML} does not exist!")
        sys.exit(1)

    if not CHROME_PATH.exists():
        print(f"Error: Chrome not found at {CHROME_PATH}")
        sys.exit(1)

    print(f"Compiling PDF via headless Chrome from {INPUT_HTML}...")
    args = [
        str(CHROME_PATH),
        "--headless=new",
        "--disable-gpu",
        "--no-pdf-header-footer",
        f"--print-to-pdf={OUTPUT_PDF}",
        str(INPUT_HTML.resolve().as_uri())
    ]
    
    res = subprocess.run(args, capture_output=True, text=True)
    if res.returncode != 0:
        print(f"Chrome PDF generation failed with code {res.returncode}:")
        print(res.stderr)
        sys.exit(res.returncode)

    if not OUTPUT_PDF.exists():
        print("Error: Output PDF was not created!")
        sys.exit(1)

    pdf_size = OUTPUT_PDF.stat().st_size
    print(f"Successfully generated PDF: {OUTPUT_PDF} ({pdf_size:,} bytes / {pdf_size / 1024 / 1024:.2f} MB)")

    # Copy to dist directory as well
    DIST_PDF.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(OUTPUT_PDF, DIST_PDF)
    print(f"Copied PDF to distribution folder: {DIST_PDF}")

if __name__ == "__main__":
    main()
