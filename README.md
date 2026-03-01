# ReProccer Evolved
ReProccer Reborn, rewritten on C# with Mutagen for Synthesis patching framework.

# Installation from Git repository
* Download and install [Synthesis](https://github.com/Mutagen-Modding/Synthesis/releases)
* In Synthesis, on the top left row of icons press "Git Repository".
* Find ReProccer Evolved in the list, and click "Add patcher" and then "Confirm".

 <b>KEEP IN MIND</b></br>
 Due to the nature of this installation method, all patcher files will be overwritten when update take place - <b>including rules files</b>.</br>
 To prevent losing changes in rule files keep them in the user data directory - it will be created upon the first patching session in your Documents (DRIVE_LETTER:\Users\USER_NAME\Documents).

# Installation as a solution (from a package)
* Download and install [Synthesis](https://github.com/Mutagen-Modding/Synthesis/releases)
* Download the latest release of [ReProccer Evolved](https://github.com/Ingvion/reproccer-evolved/releases) (source files)
* Unzip it to a directory of your choice.
* In Synthesis, on the top left row of icons press "Local Solution".
* Press "Existing", and specify path to the ReProccerEvolved.sln file in the directory with unzipped package.
* Set the patcher name (any) on top left, and press "Confirm" (if "Confirm" is not active click on the "Patcher Projects" field).

<b>Note</b>, that package-based installation does not track the patcher version and can only be updated manually.

# Important
* Do not group ReProccer Evolved with other patchers.
* Always run ReProccer Evolved last.


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
    
  Drop everything in a container, open the console, click the container and print <b>resetinv</b>).</br>

