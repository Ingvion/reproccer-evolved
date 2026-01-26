# Reproccer Evolved
Reproccer Reborn, rewritten on C# with Mutagen, to be used with Synthesis patching framework.

# Installation
* Download and install [Synthesis](https://github.com/Mutagen-Modding/Synthesis/releases)
* In Synthesis, on the top left row of icons press "Git Repository", find ReProccer Evolved in the list, and click "Add patcher".

<b>IMPORTANT</b></br>
Due to the nature of this installation method, all patcher files will be overwritten when update take place - <b>including rules files</b>.</br>
To prevent losing changes in rule files keep them in the user data directory - you can set one on the patcher settings page:
* root directory for the user data dir is your Documents (DRIVE_LETTER:\Users\USER_NAME\Documents), you only need to specify the user data dir name in the field
* user data dir must exist, the patcher does not create it for you
* user data dir must contain the "rules" folder (for modified rule files), and "locales" folder (for modified language strings files)

# ReProccer Evolved and legacy rules/strings
ReProccer Reborn rules and strings files are fully compatible with ReProccer Evolved. No modifications required.

# Migrating from ReProccer Reborn
Keep in mind, that zEdit/UPF and Synthesis each have their own records cache - which means formIDs of ReProccer generated records with a high degree of probability will not match between patches.
1. Make a backup of your savegame
2. Make a backup of the ReProccer Reborn patch
3. Dispose of all ReProccer generated items:
  * Refined Silver weapons
  * Custom crossbows (Light, Siege, Recurve, Muffled)
  * Dreamcloth gear
  * Custom ammo (Tempered, Hardened, Barbed and Heavyweight bolts, Fire, Frost, Shock and Siphoning bolts, Ashen and Explosive arrows)
    
Drop everything in a container, open the console, click the container and print <b>resetinv</b>).
