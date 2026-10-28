"""PDF-to-Unity static asset compiler.

Pipeline (SPEC-PU-001 §1):
    frontend  (PDF objects + content streams -> Document IR)
    ir        (engine independent, versioned)
    optimizer (IR -> ordered, tessellated render batches per target)
    precompose(PDFium region rasterisation for effects that cannot be drawn natively)
    package   (Asset Exchange Package consumed by the Unity Editor generator)
"""

__version__ = "0.1.0"

# Bumped whenever the serialized IR or the exchange package layout changes.
IR_SCHEMA_VERSION = 1
PACKAGE_SCHEMA_VERSION = 1
