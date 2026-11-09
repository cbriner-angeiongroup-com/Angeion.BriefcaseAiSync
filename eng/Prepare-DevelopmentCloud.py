"""Provision only app-owned development credentials/directory objects in approved shared services.

Values stay in memory and HTTPS bodies. Receipts contain resource metadata only.
Run only with explicit session authorization for development provisioning.
"""
import base64
import datetime
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parent.parent
RECEIPT = ROOT / "artifacts/development-deployment/shared-resources.json"
VAULT = "https://angdevkeyvault.vault.azure.net"
PREFIX = "briefcase-sync-development"
GRAPH = "https://graph.microsoft.com/v1.0"
records = json.loads(RECEIPT.read_text(encoding="utf-8")) if RECEIPT.exists() else []
tokens = {}


class CloudError(Exception):
    def __init__(self, status, code):
        self.status, self.code = status, code


def az(*args):
    result = subprocess.run([shutil.which("az") or "az", *args], capture_output=True, text=True)
    if result.returncode:
        raise CloudError(0, "AZURE_CLI_FAILED")
    return result.stdout.strip()


def token(resource):
    if resource not in tokens:
        tokens[resource] = az("account", "get-access-token", "--resource", resource,
                              "--query", "accessToken", "-o", "tsv")
    return tokens[resource]


def request(method, url, resource, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={
        "Authorization": "Bearer " + token(resource), "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=90) as response:
            raw = response.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as error:
        try:
            code = json.loads(error.read()).get("error", {}).get("code", "HTTP_FAILURE")
        except Exception:
            code = "HTTP_FAILURE"
        raise CloudError(error.code, code if re.fullmatch(r"[A-Za-z0-9_.-]{1,100}", code) else "HTTP_FAILURE") from None


def record(kind, name, status, **metadata):
    item = dict(kind=kind, name=name, status=status, **metadata)
    records.append(item)
    RECEIPT.parent.mkdir(parents=True, exist_ok=True)
    RECEIPT.write_text(json.dumps(records, indent=2), encoding="utf-8")
    print(json.dumps(item), flush=True)


def put_secret(alias, value):
    name = PREFIX + "-" + alias
    url = VAULT + "/secrets/" + name + "?api-version=7.4"
    try:
        existing = request("GET", url, "https://vault.azure.net")
        if not existing.get("attributes", {}).get("enabled", True):
            raise CloudError(0, "EXISTING_SECRET_DISABLED")
        record("KeyVaultSecret", name, "reused", uri=VAULT + "/secrets/" + name)
        return
    except CloudError as error:
        if error.status != 404:
            raise
    request("PUT", url, "https://vault.azure.net", {
        "value": value, "attributes": {"enabled": True},
        "tags": {"application": "briefcase-sync", "environment": "development"}})
    record("KeyVaultSecret", name, "created", uri=VAULT + "/secrets/" + name)


def application(display_name, web=False):
    query = urllib.parse.urlencode({"$filter": "displayName eq '" + display_name + "'"})
    found = request("GET", GRAPH + "/applications?" + query, "https://graph.microsoft.com")["value"]
    if len(found) > 1:
        raise CloudError(0, "AMBIGUOUS_APPLICATION")
    roles = [{"allowedMemberTypes": ["User"], "description": description,
              "displayName": value, "id": str(uuid.uuid5(uuid.NAMESPACE_URL, display_name + "/" + value)),
              "isEnabled": True, "value": value}
             for value, description in [("Sync.View", "View integration operations"),
                                        ("Sync.Preview", "Preview and reveal source values"),
                                        ("Sync.Configure", "Configure integration settings")]] if web else []
    app = found[0] if found else request("POST", GRAPH + "/applications", "https://graph.microsoft.com", {
        "displayName": display_name, "signInAudience": "AzureADMyOrg", "appRoles": roles})
    record("EntraApplication", display_name, "reused" if found else "created", appId=app["appId"], objectId=app["id"])
    query = urllib.parse.urlencode({"$filter": "appId eq '" + app["appId"] + "'"})
    principals = request("GET", GRAPH + "/servicePrincipals?" + query, "https://graph.microsoft.com")["value"]
    principal = principals[0] if principals else request("POST", GRAPH + "/servicePrincipals", "https://graph.microsoft.com", {
        "appId": app["appId"], "appRoleAssignmentRequired": web})
    record("EntraServicePrincipal", display_name, "reused" if principals else "created", objectId=principal["id"])
    if web:
        # Never reset existing credentials; create a new credential only when our vault reference is absent.
        secret_url = VAULT + "/secrets/" + PREFIX + "-entra-client-secret?api-version=7.4"
        try:
            request("GET", secret_url, "https://vault.azure.net")
            put_secret("entra-client-secret", "")
        except CloudError as error:
            if error.status != 404:
                raise
            expires = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=90)).isoformat()
            credential = request("POST", GRAPH + "/applications/" + app["id"] + "/addPassword", "https://graph.microsoft.com", {
                "passwordCredential": {"displayName": PREFIX + "-web", "endDateTime": expires}})
            put_secret("entra-client-secret", credential["secretText"])
            record("EntraCredential", display_name, "created", keyId=credential["keyId"], expiresAt=expires)
    else:
        url = GRAPH + "/applications/" + app["id"] + "/federatedIdentityCredentials"
        existing = request("GET", url, "https://graph.microsoft.com")["value"]
        if not any(item["name"] == "github-development" for item in existing):
            request("POST", url, "https://graph.microsoft.com", {
                "name": "github-development", "issuer": "https://token.actions.githubusercontent.com",
                "subject": "repo:cbriner-angeiongroup-com/Angeion.BriefcaseAiSync:environment:development",
                "audiences": ["api://AzureADTokenExchange"]})
            record("FederatedIdentityCredential", "github-development", "created", appId=app["appId"])
        else:
            record("FederatedIdentityCredential", "github-development", "reused", appId=app["appId"])


def main():
    account = json.loads(az("account", "show", "-o", "json"))
    if account["id"] != "f180d56c-6007-4ca5-ad78-bf0765f5d6eb":
        raise CloudError(0, "WRONG_DEVELOPMENT_SUBSCRIPTION")
    settings_path = Path(os.environ["APPDATA"]) / "Microsoft/UserSecrets/briefcase-sync-worker/secrets.json"
    settings = json.loads(settings_path.read_text(encoding="utf-8-sig"))
    if settings.get("SmartAdvocate:ApiUrl") != "https://cwapps.n8protect.com/Development/VendorSmartAdvocate/":
        raise CloudError(0, "SOURCE_DEVELOPMENT_ENDPOINT_REQUIRED")
    if settings.get("Briefcase:ApiUrl") != "https://fbtrial.my.salesforce.com/":
        raise CloudError(0, "DESTINATION_TRIAL_ENDPOINT_REQUIRED")
    bindings = {"sa-username": "SmartAdvocate:Username", "sa-password": "SmartAdvocate:Password",
                "briefcase-client-id": "Briefcase:ClientId", "briefcase-client-secret": "Briefcase:ClientSecret"}
    if any(not settings.get(key) for key in bindings.values()):
        raise CloudError(0, "LOCAL_DEVELOPMENT_CREDENTIALS_REQUIRED")
    for alias, key in bindings.items():
        put_secret(alias, settings[key])
    put_secret("state-key", base64.b64encode(secrets.token_bytes(32)).decode())
    key_name = PREFIX + "-web-protection"
    key_url = VAULT + "/keys/" + key_name
    try:
        request("GET", key_url + "?api-version=7.4", "https://vault.azure.net")
        status = "reused"
    except CloudError as error:
        if error.status != 404:
            raise
        request("POST", key_url + "/create?api-version=7.4", "https://vault.azure.net", {
            "kty": "RSA", "key_size": 2048, "key_ops": ["wrapKey", "unwrapKey"], "attributes": {"enabled": True}})
        status = "created"
    record("KeyVaultKey", key_name, status, uri=key_url)
    for name, web in [("Angeion.BriefcaseAiSync-development", True),
                      ("Angeion.BriefcaseAiSync-development-deploy", False)]:
        try:
            application(name, web)
        except CloudError as error:
            record("DirectoryPreparation", name, "blocked", code=error.code, httpStatus=error.status)


if __name__ == "__main__":
    try:
        main()
    except CloudError as error:
        record("Preparation", PREFIX, "blocked", code=error.code, httpStatus=error.status)
        raise SystemExit(1)
    except Exception:
        record("Preparation", PREFIX, "blocked", code="UNEXPECTED_PREPARATION_FAILURE")
        raise SystemExit(1)
