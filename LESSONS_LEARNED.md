# Reusable release lessons

## V30 — packaged Local AI readiness and Drive discovery

- **Observed:** The first status pulse showed Local AI as unavailable on a clean customer package even though the Qwen model was bundled.
- **Cause:** Readiness detection inspected only the per-user models folder and ignored `ARCHESTRO_AI_MODEL_PATH` and the package's `AI/Models` directory.
- **Fix:** Probe the configured model path, packaged model location, and user model folder. Assert the readiness source in V30 QA and confirm it in the clean-install UI.
- **Reusable check:** Test from the staged/package directory, not only the developer tree; assert a real runtime/model file is discoverable.

- **Observed:** Google Drive Desktop could not be exercised against a real synced folder on this machine.
- **Fix/QA:** Keep synthetic configured-root detection deterministic and separately report the count of actual machine roots. Never describe synthetic discovery as a live owner Drive smoke.

## V30 — native transcription intermittent failure

- **Observed:** One final system QA run returned a transcript-finalization failure, while the bridge process exited successfully and produced transcript files. A fresh isolated retry passed with `native-whisper`.
- **Cause status:** Not established. The transcription service did not surface its caught exception to the QA receipt.
- **Reusable action:** Keep the first failed attempt in evidence, retry only with fresh isolated data, and do not label the hidden exception as diagnosed. Improve exception-to-diagnostics propagation before treating recurrence as resolved.
