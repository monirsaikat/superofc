# Devices (ZKTeco terminals)

## Adding a terminal
On **Devices**, select **Add device** and enter its IP address, port (usually 4370) and the communication key if the terminal has one. The key is stored encrypted. Select **Test connection** to check it.

## Syncing
Punches are fetched automatically every few minutes; set the interval under **Settings › Policies**. **Sync now** fetches immediately. Each card shows the last sync and any problem. If a terminal's clock is wrong, a warning offers **Fix now** to set it from this PC.

## Device users and employees
**Device users** lists who is enrolled on each terminal and which employee they belong to. Link them by employee code in one click, send missing employees to a terminal, or delete users from it. Fingerprints can be backed up to SuperOfc and copied to another terminal.

## Unknown punches
Punches from a device user who is not linked to an employee go to **Unlinked punches**. Assign them to the right employee, or discard them.

## Importing from USB or a file
If a terminal is not on the network, export its attendance to a USB stick (`_attlog.dat`) and use **Import punches**. CSV and Excel files work too. You see new, duplicate and unlinked punches before anything is saved.
