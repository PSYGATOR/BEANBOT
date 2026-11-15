import base64

print("Epic Games Basic Auth Extractor")
print("-------------------------------")

auth = input("Paste your Basic Auth key: ").strip()

try:
    if auth.lower().startswith("basic "):
        auth = auth[6:]

    decoded = base64.b64decode(auth).decode("utf-8")

    epic_id, epic_secret = decoded.split(":", 1)

    print("\nSuccessfully extracted!")
    print("Epic ID:", epic_id)
    print("Epic Secret ID:", epic_secret)

except Exception:
    print("\nInvalid Basic Auth key")

input("\nPress Enter to exit...")