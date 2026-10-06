# V1.0.0 offline package cleanliness recheck

The canonical `ARCHSTRO_MEETING_VAULT_1_0_0_CUSTOMER_OFFLINE_PACKAGE_FINAL2.zip` was hashed without modification. The automated path/content scan found no owner paths, API-key patterns, settings, databases, reports, or logs. It found one bundled 1,819,586-byte WAV at `App/SpeakerModels/0-four-speakers-zh.wav` (SHA-256 `bedf036caed208386c67b4ef4b11f83d74dd0d420b102163a1c33cd09cde7010`). This is the public sherpa-onnx four-speaker demo asset distributed with the speaker model, not a recording from the owner's meeting library ([upstream asset reference](https://github.com/k2-fsa/sherpa-onnx/blob/master/kotlin-api-examples/run.sh)).

The accepted V1.0.0 package and its artifacts remain unchanged. V1.1.0 omits this demo recording from its customer payload; the speaker-model runtime does not require it.
