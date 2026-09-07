# VALIDATION_R5.19.4_SECURE_NAS
- NAS username/password fields added to NAS & Repository Settings.
- Password is never stored in browser localStorage.
- Control Plane encrypts credential payload with each Agent's RSA-3072 public key using OAEP-SHA256.
- Agent decrypts in memory and stores credential using Windows DPAPI LocalMachine.
- Repository operations automatically establish an SMB session when a credential exists.
- “NAS Bağlantısını Test Et” runs from the selected Agent and verifies directory read plus temporary-directory create/delete write access.
- VERIFY adds a secure NAS invariant gate.
