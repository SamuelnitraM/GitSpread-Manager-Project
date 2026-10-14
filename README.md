# GitSpread

**Tes projets git, rangés par compte GitHub, sans taper de commande.**

GitSpread est une application Windows qui gère tous tes projets git et tous tes comptes GitHub depuis une seule fenêtre. GitSpread affiche à chaque fois les commandes git qu'il exécute

Son point fort est le **multi-comptes**. Si tu as un compte personnel et un compte professionnel, GitSpread utilise automatiquement le bon compte pour chaque projet, sans jamais toucher à ta configuration git.

---

## Sommaire

- [Prérequis](#prérequis)
- [Installation et lancement](#installation-et-lancement)
- [Premier lancement](#premier-lancement)
- [Comment ça marche](#comment-ça-marche)
- [L'interface en un coup d'œil](#linterface-en-un-coup-dœil)
- [Fonctionnalités](#fonctionnalités)
  - [Comptes GitHub](#1-comptes-github)
  - [Projets locaux](#2-projets-locaux)
  - [Commit](#3-commit)
  - [Récupérer, Tirer, Pousser](#4-récupérer-tirer-pousser)
  - [Branches](#5-branches)
  - [Qui signe les commits](#6-qui-signe-les-commits)
  - [Publier un projet sur GitHub](#7-publier-un-projet-sur-github)
  - [Changer le repo lié, plusieurs repos par projet](#8-changer-le-repo-lié-plusieurs-repos-par-projet)
  - [Repos sur GitHub](#9-repos-sur-github)
  - [Collaborateurs et repos partagés](#10-collaborateurs-et-repos-partagés)
  - [Historique des branches](#11-historique-des-branches)
  - [Fichiers d'un commit](#12-fichiers-dun-commit)
  - [Parties repliables](#13-parties-repliables)
  - [Paramètres](#14-paramètres)
- [Garde-fous](#garde-fous)
- [Où sont stockées les données](#où-sont-stockées-les-données)
- [Raccourcis clavier](#raccourcis-clavier)
- [Dépannage](#dépannage)
- [Pour les développeurs](#pour-les-développeurs)

---

## Prérequis

| Outil | Pourquoi | Installation |
|---|---|---|
| **Windows 10 ou 11** | GitSpread est une application WPF | — |
| **git** | Toutes les opérations sur les projets | <https://git-scm.com> |
| **GitHub CLI (`gh`)** | Connexion aux comptes, gestion des repos | <https://cli.github.com> |

`git` et `gh` doivent être accessibles dans le `PATH`. Tu n'as pas besoin de savoir t'en servir : GitSpread les pilote pour toi.

Le SDK .NET 10 n'est nécessaire **que pour compiler** GitSpread : l'exécutable publié est autonome.

## Installation et lancement

**Avec l'exécutable :** copie `GitSpread.exe` où tu veux, puis double-clique dessus. Il n'y a rien à installer.

**Depuis les sources :**

```powershell
git clone https://github.com/SamuelnitraM/GitSpread-Manager-Project.git
cd GitSpread-Manager-Project
.\publish.ps1
```

`publish.ps1` lance d'abord tous les tests. S'ils sont tous verts, il génère `dist\GitSpread.exe`, un exécutable unique et autonome. Il refuse de remplacer l'exécutable si GitSpread est ouvert.

## Premier lancement

1. GitSpread propose automatiquement les dossiers de projets courants trouvés sur ta machine (`C:\xampp\htdocs`, `source\repos`, `Documents\GitHub`, `Projets`…). Tu peux les ajuster dans **Paramètres › Projets**.
2. Si aucun compte n'est connecté, clique sur **+** à côté des onglets pour connecter ton premier compte GitHub (voir [Comptes GitHub](#1-comptes-github)).
3. GitSpread analyse tes dossiers et range chaque projet dans l'onglet du compte auquel il appartient.

## Comment ça marche

### Un onglet par compte

GitSpread lit l'adresse du dépôt distant (le *remote*) de chaque projet pour savoir à quel compte GitHub il appartient. Un projet dont le remote est `github.com/SamuelnitraM/...` apparaît dans l'onglet **SamuelnitraM**.

Les projets qui n'appartiennent à aucun compte connecté vont dans l'onglet **Non liés / autres**. C'est le cas :

- d'un dossier sans remote, ou pas encore suivi par git ;
- d'un dépôt hébergé ailleurs que sur un compte connecté.

Les remotes HTTPS comme SSH sont reconnus, y compris les alias d'hôte définis dans `~/.ssh/config`.

### Le bon compte pour chaque projet, sans configuration

Avec plusieurs comptes, git n'utilise normalement qu'**un seul** jeu d'identifiants. Envoyer sur le repo de ton autre compte échoue alors, ou pire, passe avec le mauvais compte.

GitSpread règle ce problème à chaque commande réseau (Récupérer, Tirer, Pousser, Cloner…) :

1. il demande à `gh` le token du compte propriétaire du projet (`gh auth token --user <compte>`) ;
2. il transmet ce token **au seul processus git lancé**, par des variables d'environnement (`GIT_CONFIG_COUNT`, `http.extraHeader`), limitées à github.com ;
3. il désactive pour cet appel le gestionnaire d'identifiants et toute fenêtre de saisie, pour qu'un échec soit propre plutôt que bloquant.

Ce mécanisme a trois conséquences :

- ta configuration git globale **n'est jamais modifiée** ;
- les tokens **ne sont jamais écrits sur le disque** par GitSpread : ils restent dans le trousseau Windows géré par `gh` ;
- le compte actif de `gh` (celui de ton terminal) n'a aucune importance pour GitSpread.

### Tout est expliqué

Les assistants affichent, avant de lancer quoi que ce soit, la liste des commandes qu'ils vont exécuter (« Ce que GitSpread va exécuter »). Après chaque action, un **compte rendu** donne :

- le résultat en clair ;
- les commandes exécutées ;
- la sortie complète de git, repliée par défaut.

Les erreurs fréquentes de git sont traduites en explications compréhensibles.

## L'interface en un coup d'œil

```
┌───────────────────────────────────────────────────────────────────────────┐
│ GitSpread                     [Ajouter un projet] [Paramètres] [Actualiser]│
├───────────────────────────────────────────────────────────────────────────┤
│ ● SamuelnitraM ⌂5 ☁11 ⇄ │ SmartDev-Web ⌂2 ☁6 │ Non liés / autres ⌂0 │ +    │
├───────────────────────────────────────────────────────────────────────────┤
│ [Projets locaux 5 | Repos sur GitHub 11]                                  │
│ ┌─ Liste ──────────────────────┐ ┌─ Panneau du projet choisi ───────────┐ │
│ │ Calculator   HTTPS           │ │ Actions · Liens · Branches ·         │ │
│ │ GitSpread    HTTPS           │ │ Qui signe les commits · État         │ │
│ └──────────────────────────────┘ └──────────────────────────────────────┘ │
│ Historique ─────────────────────────────────────────── [Agrandir] ↻ ⌄     │
│ (graphe des branches du projet choisi)                                    │
├───────────────────────────────────────────────────────────────────────────┤
│ 2 comptes · actif dans gh : SamuelnitraM · 6 projets trouvés      Journaux │
└───────────────────────────────────────────────────────────────────────────┘
```

**L'en-tête** contient trois boutons :

- **Ajouter un projet** ajoute un dossier où qu'il soit, même sans git ;
- **Paramètres** ouvre les réglages (Ctrl+,) ;
- **Actualiser** relance la détection des comptes, des repos et des projets (F5).

**Les onglets :**

- Il y a un onglet par compte connecté, plus **Non liés / autres**.
- Chaque onglet indique le nombre de projets sur ce poste (⌂) et de repos sur GitHub (☁).
- Le compte **actif dans gh** a un point vert clignotant. Le bouton **switch** change le compte actif de `gh`.
- Tu peux **glisser un onglet** pour changer l'ordre ; l'ordre est mémorisé.
- **Clic droit sur un onglet de compte :** rendre actif, autorisations, profil GitHub, déconnecter.
- **Déposer un projet sur un onglet de compte** le rattache à ce compte (voir [§ 8](#8-changer-le-repo-lié-plusieurs-repos-par-projet)).

**La barre d'état** résume les comptes et l'analyse des dossiers. Le lien **Journaux** ouvre le dossier des logs.

---

## Fonctionnalités

### 1. Comptes GitHub

**Connecter un compte** (bouton **+** ou Ctrl+N). Un assistant reprend une à une les questions de `gh auth login`, traduites et expliquées :

- **hôte :** GitHub.com ou GitHub Enterprise Server ;
- **protocole :** HTTPS (recommandé, aucune clé à gérer) ou SSH ;
- **pour SSH :** utiliser une clé existante, générer une clé `ed25519` dédiée au compte (`id_ed25519_<compte>`, avec une phrase secrète facultative), ou n'envoyer aucune clé ;
- **authentification :** par le navigateur (un code à usage unique à saisir sur github.com) ou en collant un token d'accès.

GitSpread conseille d'utiliser une fenêtre de navigation privée pour ne pas valider avec le mauvais compte. Un récapitulatif montre les commandes avant de lancer la connexion.

**Gérer un compte** (clic droit sur son onglet, ou **Paramètres › Comptes et identités**) :

- **Rendre actif dans gh** : change le compte utilisé par `gh` dans ton terminal. Ça ne change rien pour GitSpread.
- **Autorisations…** : affiche ce que GitSpread a le droit de faire sur le compte (repo, workflow, suppression de repos, clés SSH…). Tu coches ce que tu veux accorder et décoches ce que tu veux retirer, puis tu valides avec un code dans le navigateur. Si une action demande une autorisation absente (par exemple la suppression d'un repo), GitSpread propose de l'ajouter au moment voulu.
- **Voir le profil sur GitHub.**
- **Déconnecter…** : retire le token du trousseau Windows. Les projets du compte passent dans « Non liés / autres ». Rien n'est supprimé, ni repo ni fichier.

### 2. Projets locaux

**Trouver ses projets.** GitSpread parcourt les dossiers choisis, jusqu'à la profondeur réglée, en ignorant les dossiers de dépendances (`node_modules`, `vendor`, `bin`, `obj`…) et les dossiers cachés. **Ajouter un projet** ajoute un dossier isolé, avec ou sans git.

**La liste des projets** affiche, pour chaque projet :

- son nom ;
- son protocole (HTTPS ou SSH) ;
- son repo GitHub ;
- son chemin ;
- un badge « Partagé » s'il est accessible à plusieurs comptes.

Un champ filtre la liste par nom, repo ou chemin. Le **clic droit** sur un projet propose : ouvrir le dossier, ouvrir sur GitHub, copier le chemin, **masquer de la liste**, ou retirer de GitSpread (pour un projet ajouté à la main). Le dossier n'est jamais touché.

**Le panneau du projet choisi** est découpé en sections repliables.

- **Actions :**
  - pour un dossier pas encore suivi par git : **Initialiser git**, **Publier sur GitHub…** ;
  - pour un projet git : **Commit…**, **Récupérer**, **Tirer**, **Pousser**, **Nouvelle branche**, **Changer le repo lié…**.
- **Liens et partage :** le repo lié, les autres repos vers lesquels le projet envoie ses commits, et le compte utilisé pour envoyer.
- **Branches :** la branche actuelle, sa situation par rapport à GitHub, et les autres branches comparées à la branche principale.
- **Qui signe les commits :** l'identité utilisée et à quel compte GitHub les commits seront attribués.
- **État et dernier commit :** les fichiers modifiés, ajoutés ou en conflit, et le dernier commit.

**Ranger un projet sans remote.** Un projet de « Non liés » qui n'a pas encore de remote peut être **glissé sur l'onglet d'un compte**. Il est alors rangé dans les projets locaux de ce compte, sans que rien ne soit créé sur GitHub. **Remettre dans « Non liés »** annule ce rangement.

**Conflits.** Si une fusion s'arrête sur des conflits, GitSpread liste les fichiers concernés et explique quoi faire : garder la bonne version entre les marqueurs `<<<<<<<` et `>>>>>>>`. Tu as ensuite deux choix :

- **Terminer la fusion** ;
- **Abandonner**, pour revenir à l'état d'avant.

### 3. Commit

La fenêtre **Commit…** te guide pour enregistrer tes modifications.

- **Choisir les fichiers à inclure.** Les dossiers de dépendances et les fichiers générés sont repérés et décochés d'office. Si le projet n'a pas de `.gitignore`, GitSpread le signale.
- **Écrire le message**, avec un exemple pour guider.
- **Vérifier la signature :** qui signe le commit, avec un avertissement si l'adresse ne correspond pas au compte du projet. Tu peux alors corriger l'identité, ou confirmer avec **C'est bien mon adresse**.
- **Ajouter un co-auteur :** une ligne `Co-authored-by` pour qu'un autre de tes comptes apparaisse aussi comme contributeur sur GitHub.
- **Pousser sur GitHub ensuite**, en option.

### 4. Récupérer, Tirer, Pousser

- **Récupérer** (`git fetch`) regarde ce qui a changé sur GitHub sans modifier tes fichiers. Les compteurs « à envoyer » et « à récupérer » se mettent à jour.
- **Tirer** (`git pull --ff-only`) applique les nouveaux commits de GitHub, **sans jamais créer de fusion automatique**. Si une fusion est nécessaire, GitSpread l'explique au lieu de la faire en douce.
- **Pousser** (`git push`) envoie tes commits, **jamais avec `--force`**. Si le projet est lié à plusieurs repos, tu coches ceux qui doivent recevoir l'envoi ; ce choix est mémorisé. Le résultat est donné repo par repo.

### 5. Branches

**Depuis le panneau d'un projet :**

- **Nouvelle branche** : le nom est vérifié pendant la frappe, et les espaces sont remplacés par des tirets.
- **Basculer** sur une autre branche. Si tu as des modifications non commitées, GitSpread peut les **mettre de côté** puis les **restaurer** plus tard.
- **Intégrer dans main** (fusion), avec un assistant qui procède dans cet ordre :
  1. il récupère les nouveautés de GitHub ;
  2. il analyse les deux branches et liste à l'avance les **fichiers modifiés des deux côtés**, susceptibles de créer des conflits ;
  3. tu peux mettre tes modifications non commitées de côté pendant l'opération ;
  4. tu peux envoyer la branche principale sur GitHub ensuite ;
  5. tu peux supprimer la branche intégrée sur ce poste (sa version GitHub est conservée).
- **Renommer la branche principale** (par exemple `master` → `main`) ici et sur GitHub.

**Depuis l'historique** (voir [§ 11](#11-historique-des-branches)) : basculer, récupérer une branche qui n'est que sur GitHub, intégrer, pousser, renommer, supprimer, et créer une branche à partir de n'importe quel commit.

- **Renommer une branche :** elle est renommée ici et, si elle y est, sur GitHub (qui garde ses pull requests). Les autres projets de ce poste liés au même repo sont mis à jour. Pour un projet lié à plusieurs repos, GitSpread demande une confirmation pour chaque lien.
- **Supprimer une branche :** le choix par défaut est « seulement sur ce poste » ; il faut choisir explicitement « aussi sur GitHub ». GitSpread **avertit si des commits seraient perdus**. La branche principale et la branche actuelle ne peuvent pas être supprimées.

### 6. Qui signe les commits

Chaque commit enregistre un nom et un e-mail. **C'est cet e-mail qui décide à quel compte GitHub le commit est attribué.**

- Le panneau d'un projet affiche l'identité utilisée et dit clairement si les commits seront attribués au bon compte.
- Un menu propose plusieurs identités :
  - l'**adresse privée GitHub** (noreply) de chaque compte ;
  - les adresses réelles connues du compte ;
  - l'identité globale du poste ;
  - une identité personnalisée.
- Le choix s'applique **à ce projet seulement** (`git config --local`).
- Tu peux **re-signer les commits pas encore envoyés** avec la bonne identité. Le contenu et les dates restent les mêmes. Les commits déjà publiés ne sont jamais réécrits.
- Quand un projet change de compte (publication, repointage, clonage), GitSpread propose de signer désormais avec l'identité du nouveau compte.
- **C'est bien mon adresse** mémorise une adresse comme appartenant à un compte : l'avertissement ne revient plus.

### 7. Publier un projet sur GitHub

**Publier sur GitHub…** crée un repo sur le compte choisi, y relie le projet et envoie son contenu. Rien n'est envoyé avant le clic sur « Publier ».

- **Nom du repo :** vérifié pendant la frappe (disponibilité, caractères autorisés), avec une correction proposée en un clic.
- **Visibilité :** privée ou publique.
- **Description** facultative.
- **Modèle de `.gitignore`**, si le projet n'en a pas.
- **Message du premier commit**, utilisé seulement s'il reste des fichiers à commiter.
- **Identité** du projet, alignée sur le compte choisi.

### 8. Changer le repo lié, plusieurs repos par projet

**Changer le repo lié…**, ou glisser le projet sur l'onglet d'un autre compte, ouvre un assistant avec deux options.

- **Repointer (recommandé) :** le dossier garde son code et son historique, et il est envoyé dans un repo du compte cible, existant ou nouveau. Le repo d'origine reste en place. Tu choisis :
  - l'ancien lien est gardé sous le nom `old-origin`, ou retiré ;
  - toutes les branches et les tags sont envoyés, ou seulement la branche actuelle ;
  - en cas d'historique différent sur la cible, tu peux envoyer sur une nouvelle branche.
- **Transférer le repo GitHub** à l'autre compte. Les issues, les étoiles et l'historique sont conservés.

Aucun `--force` n'est utilisé. **En cas d'échec, le projet retrouve son lien d'origine.**

**Plusieurs repos pour un même projet.** Un projet peut envoyer ses commits vers plusieurs repos, par exemple une copie sur ton second compte.

- Dans **Liens et partage**, **Ajouter un lien vers un autre repo…** ajoute un repo sur ce compte ou un autre, existant ou nouveau.
- Chaque lien peut être **renommé**, **retiré** ou **défini comme principal**.
- Le projet apparaît dans l'onglet de chaque compte concerné.
- **Pousser** envoie vers les liens cochés, chacun avec le token de son compte.

### 9. Repos sur GitHub

Le bouton **Repos sur GitHub** d'un onglet liste les repos du compte, avec ceux où il est collaborateur.

- La liste montre la visibilité, la description, la date du dernier envoi, et les badges fork, archivé et collaborateur.
- Un filtre cherche par nom ou description. L'option **Afficher les archivés** montre aussi les repos archivés.
- Le clic droit propose : ouvrir sur GitHub, copier l'URL HTTPS, copier l'URL SSH.

**Le panneau d'un repo** est découpé en sections repliables.

- **Au quotidien :**
  - les projets de ce poste liés au repo ;
  - **Cloner en local…** : le dossier proposé est réglable, et le projet cloné est ajouté à GitSpread et signé avec l'identité du compte si tu le souhaites ;
  - **Modifier…** : description, site web, sujets.
- **Branches :** toutes les branches lues sur GitHub, avec leur couleur, leur dernier commit et leur situation par rapport à la branche principale (par exemple « 1 commit pas encore dans main · main a 2 commits de plus »). Tu peux **renommer** une branche (les projets liés sont mis à jour) ou la **supprimer** (jamais la principale, avec un avertissement si du travail n'est pas dans main).
- **Partage :** les collaborateurs et les invitations (voir [§ 10](#10-collaborateurs-et-repos-partagés)).
- **Zone sensible**, repliée par défaut et réservée aux administrateurs du repo :
  - renommer le repo ;
  - le rendre privé ou public ;
  - l'archiver ou le désarchiver ;
  - le transférer à un autre compte ;
  - le **supprimer**. La suppression demande l'autorisation « Suppression de repos », proposée au besoin. Il faut **retaper le nom complet du repo**, et GitSpread rappelle ce qui sera perdu. Les fichiers locaux ne sont jamais touchés.

### 10. Collaborateurs et repos partagés

- **Ajouter un collaborateur…** donne à un autre compte le droit d'envoyer des commits sur un repo, qui reste chez son propriétaire. Si ce compte est **connecté à GitSpread**, l'invitation est **acceptée automatiquement** avec son propre token. Sinon, la personne la reçoit par e-mail (valable 7 jours).
- Les **invitations reçues** apparaissent en haut de la liste des repos, avec **Accepter** et **Refuser**.
- Un repo partagé apparaît chez son propriétaire et chez chaque collaborateur connecté. Le projet local correspondant apparaît dans l'onglet de chaque compte qui y a accès.
- **Quitter ce repo…** renonce à l'accès de collaborateur. **Retirer** un collaborateur est possible depuis le repo du propriétaire.

### 11. Historique des branches

**L'aperçu**, en bas de chaque onglet, montre le graphe des branches du projet (ou du repo) sélectionné.

- Sa hauteur se règle en tirant son bord ; il se replie ou se déplie, et tout est mémorisé.
- Un **double-clic** sur un commit, ou le bouton **Agrandir**, ouvre l'historique en grand.

**L'historique en grand** prend la place du contenu de l'onglet. Les onglets restent accessibles, et **Retour aux projets** ramène à la liste.

- **À gauche :**
  - l'onglet **Projets** liste les projets locaux de l'onglet, puis ses repos GitHub pas encore clonés, avec un filtre ;
  - l'onglet **Branches** donne pour chaque branche sa couleur, son dernier commit et sa situation en clair (« 3 commits pas encore dans main », « à jour avec GitHub », « seulement sur GitHub »…). Il propose aussi les actions sur les branches.
- **Au centre : le graphe.**
  - Les fusions et les divergences sont dessinées.
  - La branche principale est en bleu franc et plus épais. Les branches classiques sont en bleus clairs, et les branches de maintenance (fix, hotfix, debug…) en jaune, orange ou rouge.
  - Les noms de branches ne sont jamais traduits ni modifiés.
  - Les étiquettes distinguent les branches locales, leur version sur GitHub et les tags ; ta position actuelle est marquée.
  - Une légende en mots simples se trouve en bas.
  - Les 300 derniers commits sont chargés, et **Afficher plus** charge la suite.
- **Recherche :** un champ au-dessus du graphe cherche par message, auteur, adresse, SHA, branche ou tag. Les autres commits sont estompés. Entrée et Maj+Entrée (ou les flèches) passent au résultat suivant ou précédent, et Échap efface la recherche.
- **À droite :** le commit choisi, avec :
  - son message complet, son auteur, sa date et son SHA ;
  - **Créer une branche à partir de ce commit…** ;
  - les branches qui le contiennent ;
  - ses fichiers (voir [§ 12](#12-fichiers-dun-commit)) ;
  - les **modifications** de chaque fichier, avec les lignes ajoutées en vert et les lignes retirées en rouge.

**Repos pas encore clonés.** L'historique d'un repo GitHub absent de ce poste s'affiche aussi, en lecture seule. GitSpread en garde une **copie légère** : l'historique seul, sans token sur le disque. Le contenu d'un fichier n'est téléchargé que quand tu l'ouvres. La copie est mise à jour à l'ouverture et avec **Relire**, et un bouton propose **Cloner en local**.

### 12. Fichiers d'un commit

Dans l'historique en grand, la section des fichiers offre deux vues.

- **Modifiés :** les fichiers ajoutés (A), modifiés (M), renommés (R) ou supprimés (D) par le commit.
- **Tous les fichiers :** l'**arborescence complète du projet tel qu'il était à ce commit**.
  - Chaque dossier indique son nombre de fichiers et combien ont été modifiés par ce commit. Les fichiers modifiés gardent leur pastille.
  - Les dossiers ne se construisent qu'à l'ouverture, donc ça reste rapide même avec des milliers de fichiers.
  - Un filtre par nom ou chemin donne une liste à plat.

**La visionneuse** affiche le fichier choisi.

- Un fichier modifié montre d'abord **ses modifications**, puis **Voir le fichier entier**.
- Un fichier texte s'affiche avec les numéros de ligne ; au-delà de 5 000 lignes, il est tronqué et le message le dit.
- Une image (png, jpg, gif, bmp, ico) s'affiche en aperçu.
- Pour un fichier binaire ou trop gros, seuls son type et sa taille sont indiqués.

**Actions sur le fichier affiché :**

- **Copier le chemin.**
- **Enregistrer cette version sous…** : écrit une copie du fichier tel qu'il était à ce commit, où tu veux. Le projet n'est pas touché.
- **Restaurer dans le projet…** : remet le fichier comme il était à ce commit (`git restore --source`). L'historique ne change pas : la restauration apparaît comme un changement normal, à commiter ou à annuler. Il faut confirmer, et la restauration est **refusée si le fichier a des modifications pas encore commitées**, pour ne rien perdre. Elle n'est pas proposée pour un repo pas encore cloné.

### 13. Parties repliables

Toutes les parties qui peuvent devenir longues se replient d'un clic sur leur titre :

- les sections des panneaux ;
- la liste des liens et les autres branches ;
- le détail du commit, ses branches, ses fichiers et les modifications ;
- les commandes et la sortie des comptes rendus.

Leur état est mémorisé et vaut pour tous les projets et tous les repos. La mémorisation se désactive ou se réinitialise dans les paramètres.

### 14. Paramètres

**Paramètres** (Ctrl+,) regroupe tous les réglages. Chaque réglage s'applique immédiatement. Si les dossiers ont changé, une nouvelle analyse est lancée à la fermeture.

| Page | Réglages |
|---|---|
| **Apparence** | Vue principale : liste des projets, ou arborescence (chaque onglet s'ouvre une fois sur l'historique en grand) · aperçu de l'historique affiché ou masqué · thème (du système, clair, sombre) · dates relatives (« il y a 2 j ») ou complètes (« 13/10/2026 14:05 ») · mots qui font d'une branche une branche de maintenance |
| **Git et GitHub** | Afficher ou masquer les commandes git · visibilité par défaut des nouveaux repos · adresse proposée pour signer les commits : privée (noreply) ou réelle · dossier proposé pour cloner · nombre de commits chargés dans l'historique (100 à 1000) |
| **Projets** | Dossiers analysés · profondeur de recherche · dossiers ignorés · projets ajoutés à la main (Retirer) · projets masqués (Afficher) |
| **Comptes et identités** | Chaque compte : état, autorisations, rendre actif, profil, déconnecter, et ses adresses e-mail (privée, publique et celles que tu as confirmées, à ajouter ou retirer) · connecter un compte · **identité globale du poste** (nom et e-mail utilisés par les projets sans identité propre) |
| **Avancé** | Versions de git et gh détectées · mémorisation des parties repliables (activer, tout réinitialiser) · place occupée par les copies légères et **Vider le cache** · emplacement des paramètres et des journaux · **Réinitialiser les paramètres** (tes dossiers, projets, ordre des onglets et e-mails connus sont conservés) |

---

## Garde-fous

- **Jamais de `--force`** : un historique différent sur GitHub n'est jamais écrasé.
- **Jamais de fusion implicite** : Tirer refuse de fusionner (`--ff-only`), et une intégration s'annonce avant de commencer.
- **Les commits déjà publiés ne sont jamais réécrits.** Seuls les commits pas encore envoyés peuvent être re-signés.
- **Tes fichiers locaux ne sont jamais supprimés**, même quand tu supprimes un repo, retires un projet ou déconnectes un compte.
- **Les actions difficiles à défaire demandent une confirmation.** Dans ce cas, « Non » est le choix par défaut. Supprimer un repo demande de retaper son nom.
- **Ta configuration git globale n'est pas modifiée** par les opérations réseau. Seule la page « Identité globale du poste » la change, et seulement si tu enregistres.
- **Aucun secret dans les fichiers de GitSpread** : les tokens restent dans le trousseau Windows géré par `gh`, et ils sont masqués dans les journaux.
- **Échecs propres** : git ne peut jamais ouvrir de fenêtre de mot de passe qui bloquerait l'application. Si un repointage échoue, l'ancien lien est rétabli.
- **Un dépôt reçu d'ailleurs ne lance rien quand GitSpread le lit** : `core.fsmonitor` est désactivé, les modifications sont affichées sans outil de diff ni conversion définis par le dépôt, et l'aperçu d'un fichier montre son contenu brut.
- **Le token n'est jamais envoyé sans vérification du certificat HTTPS**, même si la configuration du dépôt la désactive.
- **Les noms de fichiers sont pris tels quels**, jamais comme des motifs : `[id].tsx` ne désigne pas aussi `i.tsx`.
- **Les noms de branches et de repos sont vérifiés et encodés** avant chaque appel à l'API GitHub. Supprimer `fix#12` ne peut pas supprimer `fix`.
- **Les secrets restent hors des journaux** : tokens et phrase secrète d'une clé SSH sont masqués. Les aperçus d'images, qui peuvent venir de repos privés, sont effacés au lancement et à la fermeture.
- **À savoir sur les hooks git** : comme avec git en ligne de commande, les hooks du projet (`pre-push`, `post-merge`…) s'exécutent pendant Pousser ou Tirer. Ils peuvent lire l'environnement de git, qui contient alors le token du compte. N'utilise donc que des hooks de confiance.

## Où sont stockées les données

| Donnée | Emplacement |
|---|---|
| Paramètres (JSON, sans secret) | `%AppData%\GitSpread\settings.json` |
| Journaux | `%LocalAppData%\GitSpread\logs` |
| Copies légères des repos non clonés | `%LocalAppData%\GitSpread\cache` (vidable dans les paramètres) |
| Tokens GitHub | Trousseau Windows, géré par `gh` |
| Aperçus d'images de l'historique | `%Temp%\GitSpread\apercus` |

Si le fichier de paramètres est illisible, GitSpread repart des valeurs par défaut et garde une copie en `settings.json.bak`.

## Raccourcis clavier

| Raccourci | Action |
|---|---|
| **F5** | Actualiser (comptes, repos, projets) |
| **Ctrl+N** | Connecter un compte GitHub |
| **Ctrl+,** | Paramètres |
| **Entrée / Maj+Entrée** | Recherche dans l'historique : résultat suivant / précédent |
| **Échap** | Effacer la recherche dans l'historique |
| **Menu ou Maj+F10** | Menu du compte, sur un onglet sélectionné |

## Dépannage

| Problème | Solution |
|---|---|
| « La GitHub CLI (gh) est introuvable » | Installe `gh` depuis <https://cli.github.com>, puis relance GitSpread. |
| « git refuse ce dossier car il appartient à un autre utilisateur Windows » | GitSpread affiche la commande `git config --global --add safe.directory …` à lancer pour autoriser ce dossier. |
| Un compte affiche « connexion à refaire » | Son token n'est plus valide : déconnecte-le puis reconnecte-le avec **+**. |
| Un projet n'apparaît pas | Vérifie **Paramètres › Projets** : dossiers analysés, profondeur, dossiers ignorés, projets masqués. Tu peux aussi l'ajouter avec **Ajouter un projet**. |
| Les commits sont attribués au mauvais compte | Panneau du projet › **Qui signe les commits** : choisis l'identité du bon compte, puis re-signe les commits pas encore envoyés. |
| Autre souci | Le lien **Journaux** de la barre d'état ouvre les logs ; les tokens y sont masqués. |

---

## Pour les développeurs

### Technologies

- **.NET 10**, WPF avec le thème Fluent, et un thème clair et un thème sombre propres à GitSpread.
- **MVVM** avec CommunityToolkit.Mvvm : les ViewModels ne dépendent pas de WPF, les fenêtres passent par `IDialogService`.
- Journaux avec **Serilog**.
- `git` et `gh` sont lancés **sans shell**. Les arguments passent par `ProcessStartInfo.ArgumentList`, donc les espaces, accents et guillemets sont gérés.

### Organisation

```
src/
  GitSpread.Core/     Logique sans interface : git, GitHub (gh), comptes, projets, paramètres
    Accounts/         Détection des comptes, connexion, autorisations
    Git/              Commandes git, historique et graphe, fichiers d'un commit, erreurs expliquées
    GitHub/           Repos, branches, collaborateurs, publication (via gh)
    Projects/         Analyse des dossiers, classement par compte, liens multiples, copies légères
    Processes/        Lancement de processus (et masquage des secrets dans les journaux)
    Settings/         Paramètres JSON
    Ssh/              Clés et configuration SSH
  GitSpread.App/      Application WPF : fenêtres, vues, ViewModels, thèmes
tests/
  GitSpread.Tests/      Tests du Core (xUnit), dont de vrais dépôts git temporaires
  GitSpread.App.Tests/  Tests des ViewModels (gh simulé, git réel)
spikes/AuthSpike/       Essai qui a validé la stratégie d'authentification multi-comptes
```

### Compiler et tester

```powershell
dotnet build
dotnet test GitSpread.slnx
.\publish.ps1          # tests puis dist\GitSpread.exe (autonome)
```

Les tests simulent GitHub avec un **dépôt nu local**, atteint par une vraie adresse `https://github.com/...` grâce à `url.<chemin>.insteadOf`. Git s'exécute donc pour de vrai, seules les commandes `gh` sont simulées. Aucun test ne touche à GitHub ni à ta configuration git.

L'intégration continue (GitHub Actions, `.github/workflows/ci.yml`) compile le projet et lance tous les tests sous Windows à chaque envoi sur `main` et à chaque pull request.
