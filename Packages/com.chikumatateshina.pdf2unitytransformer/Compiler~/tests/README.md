# Compiler optimization regressions

From the repository root, using the Python environment with `Compiler~/requirements.txt` installed:

```powershell
python -m unittest discover -s Packages/com.chikumatateshina.pdf2unitytransformer/Compiler~/tests
```

These tests use synthetic data and do not require the local PDF corpus or Unity.
They cover deep clip ancestry, cached and empty clips, intersection behavior,
triangle winding, binary geometry compatibility, and PDF cleanup on failure.
