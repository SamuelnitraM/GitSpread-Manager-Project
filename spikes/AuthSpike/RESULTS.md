# Étape 0 — Spike d'authentification multi-comptes

**Date :** 2026-10-13 · **Env :** Windows 11, git 2.36.1, gh 2.102.0, .NET 10.0.401
**Comptes :** SmartDev-Web (actif dans gh), SamuelnitraM (inactif) · **Helper git :** Git Credential Manager (`manager-core`, config système)

Lancer : `dotnet run --project spikes/AuthSpike` (non destructif : `push --dry-run`, aucun repo créé).

## Résultats

| Stratégie | SmartDev-Web → son repo | SmartDev-Web → repo de l'autre | SamuelnitraM → repo de l'autre | SamuelnitraM → son repo |
|---|---|---|---|---|
| Référence (GCM, config actuelle) | ✓ | ✗ refusé (SmartDev-Web) | — | — |
| **A. Token injecté via `http.extraHeader`** | ✓ | ✗ refusé (SmartDev-Web) | ✗ refusé (SamuelnitraM) | **✓** |
| B. Helper `gh auth git-credential` + username dans l'URL | ✓ | ✗ refusé | ✗ pas de mot de passe | ✗ pas de mot de passe |

- Compte actif gh et config git **inchangés** après les tests.
- Chemin de travail avec espaces et accents : aucun problème (`ArgumentList`).

## Conclusions

1. **Le risque est confirmé.** GCM pousse toujours avec un seul compte (SmartDev-Web). Sans intervention, tout push vers un repo de SamuelnitraM échoue, ou pire, réussit avec la mauvaise identité si ce compte est collaborateur.
2. **`gh auth git-credential` ignore le username** : il ne renvoie que le token du compte *actif*. La piste « username dans l'URL » ne fonctionne donc pas avec gh.
3. **Stratégie retenue : A.** Pour chaque commande git réseau, GitSpread :
   - récupère le token via `gh auth token --user <compte>` (jamais stocké par l'app) ;
   - passe la config **par variables d'environnement** du seul processus git lancé :
     - `GIT_CONFIG_COUNT=2`
     - `credential.helper=` (vide, désactive GCM pour cet appel)
     - `http.https://github.com/.extraHeader=Authorization: Basic base64(<login>:<token>)` (limité à github.com)
   - force le non-interactif : `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never` (échec propre plutôt qu'une fenêtre bloquante).

   Avantages : pas de `gh auth switch` (aucun état global modifié, pas de risque de rester sur le mauvais compte en cas de crash), rien dans la ligne de commande (invisible dans la liste des processus), rien écrit dans `.git/config`.
4. **Garde-fou gratuit :** le message GitHub `denied to <login>` permet d'afficher précisément quel compte a été refusé.

## SSH

Aucune clé SSH GitHub sur la machine (`Permission denied (publickey)`, aucun alias `Host → github.com`). Stratégie prévue, à valider quand une clé par compte existera :

- **Lecture des remotes** : `git@github.com:owner/repo`, `ssh://git@github.com/owner/repo` et les alias (`git@github-perso:…`, résolus via `~/.ssh/config`).
- **Identité** : association compte → clé dans la config de l'app, avec `GIT_SSH_COMMAND="ssh -i <clé> -o IdentitiesOnly=yes"` par appel. Détection du compte d'une clé via `ssh -T` (« Hi <login>! »).
- **Repli** : un projet en SSH sans clé connue peut être poussé en HTTPS (stratégie A) vers la même URL convertie.

## Points annexes

- git 2.36.1 date de 2022 : une mise à jour est conseillée (correctifs de sécurité). `GIT_CONFIG_COUNT` demande ≥ 2.31, donc c'est OK.
- Logs et prévisualisation : masquer systématiquement les tokens et l'en-tête `Authorization`.
