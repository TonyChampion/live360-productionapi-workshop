#!/usr/bin/env bash
# Creates a Development JWT for each lab user and writes them all to tokens.md (replaced on every run).
# Run it from anywhere:  bash make-tokens.sh
# Same thing for Windows: make-tokens.ps1
set -euo pipefail
cd "$(dirname "$0")"

project="src/Ticketing.Api"
settings="$project/appsettings.Development.json"
out="tokens.md"
valid_for="30d"
users=("alice requester" "bob requester" "dana agent" "admin admin")

# user-jwts rewrites appsettings.Development.json on every run, even when nothing changes
# (line endings, final newline). If the Bearer settings are already there, put the file back as it was.
# If they aren't (Lab 3 Start), keep what user-jwts wrote: adding them is the point.
backup=""
if grep -q "dotnet-user-jwts" "$settings" 2>/dev/null; then
  backup="$(mktemp)"
  cp "$settings" "$backup"
fi
restore() { if [ -n "$backup" ]; then cp "$backup" "$settings"; rm -f "$backup"; fi; }
trap restore EXIT

names=(); roles=(); tokens=(); expires=""
for u in "${users[@]}"; do
  read -r name role <<< "$u"
  json="$(dotnet user-jwts create --project "$project" --name "$name" --role "$role" --valid-for "$valid_for" --output json)"
  token="$(printf '%s\n' "$json" | sed -n 's/.*"Token": *"\([^"]*\)".*/\1/p')"
  expires="$(printf '%s\n' "$json" | sed -n 's/.*"Expires": *"\([^"]*\)".*/\1/p')"
  if [ -z "$token" ]; then echo "Couldn't read a token for $name. user-jwts said:"; echo "$json"; exit 1; fi
  names+=("$name"); roles+=("$role"); tokens+=("$token")
  echo "  $name ($role)"
done

{
  echo "# Dev tokens"
  echo
  echo "Made by \`make-tokens\` on $(date '+%Y-%m-%d %H:%M'). They expire $expires."
  echo "Run the script again any time for fresh ones. This file is replaced on every run."
  echo
  echo "| User | Role | What they can do |"
  echo "|---|---|---|"
  echo "| alice | requester | Create, read, update, and comment on her own tickets |"
  echo "| bob | requester | The same, for his own. Try reading alice's ticket as bob |"
  echo "| dana | agent | Every ticket, plus assign, close, and bulk close |"
  echo "| admin | admin | Everything an agent can |"
  echo
  echo "## Lab client"
  echo
  echo "Pick the role in **Signed in as**, click **Set token**, and paste the token:"
  for i in "${!names[@]}"; do
    echo
    echo "**${names[$i]}** (${roles[$i]})"
    echo
    echo '```'
    echo "${tokens[$i]}"
    echo '```'
  done
  echo
  echo "## .http files"
  echo
  echo "Paste these over the variables at the top of any file in \`http/\`:"
  echo
  echo '```'
  for i in "${!names[@]}"; do echo "@${names[$i]} = ${tokens[$i]}"; done
  echo '```'
  echo
  echo "## Should a token ever be committed?"
  echo
  echo "These are harmless, and it's worth knowing why rather than assuming:"
  echo
  echo "- They're signed with a key in *your* user secrets, so only the API on your machine accepts them."
  echo "- The API only trusts this issuer (\`dotnet-user-jwts\`) in Development, and they expire."
  echo
  echo "A real token is a different story. A JWT is a bearer credential: whoever holds it *is* that user until it expires."
  echo "It's signed, not encrypted, so anyone can read what's inside. Never commit one, paste one into a ticket or chat, or write one to a log."
  echo "GitHub secret scanning and most CI scanners flag JWTs on push."
  echo
  echo "That's why \`tokens.md\` is in \`.gitignore\` even though these tokens don't matter: build the habit on the ones that don't."
} > "$out"

echo "Wrote $out (valid until $expires)"
