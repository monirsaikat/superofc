# Word letter templates

These four Word files are our real company letters with the employee's details replaced by **fields** such as
`{Employee.Name}`. When you issue a letter, SuperOfc fills the fields for each employee and leaves everything else
(fonts, bold, superscripts like "1st", tables, the space for the letterhead) exactly as in the Word file.

The company name and the signatory (MD Hasibur Rahman Hasan, Head Of Operations) are typed in the files as plain text.
To change them, edit the Word file and upload it again.

## How to upload

1. Open **Settings > Letter templates**.
2. Click **Add Word template** and choose one of the files below.
3. Give it a subject (used for the letter register and file names) and save.

## The templates

### NOC letter.docx

No objection certificate for an employee who is leaving for another job.

Fields: letter date, employee name, joining date, exit date, designation, he/she, his/her, him/her.

Setup: the employee needs a **joining date**, an **exit date** and a **gender** (for he/she).

### Certificate of employment.docx

Confirms that a current employee works here, with their job responsibilities and gross salary.

Fields: letter date, Mr./Ms., employee name, joining date, he/she, designation, responsibilities, gross salary
(in words and in figures).

Setup:
- Write the **responsibilities in the designation's description**, one responsibility per line. Each line becomes
  one bullet in the letter.
- Set the employee's **gender** so the letter says Mr./Ms. and he/she correctly.
- The employee needs a salary.

### Experience certificate.docx

Experience certificate for an employee who has left.

Fields: employee name, son/daughter, father's name, he/she, his/her, him/her, designation, joining date, exit date.

Setup:
- Enter the **father's name** on each employee's profile.
- Set the employee's **gender** (for son/daughter and he/she).
- The employee needs a **joining date** and an **exit date**.

### Salary certificate.docx

Salary certificate with a table of salary components.

Fields: letter date, employee name, joining date, he/she, his/her, designation, gross salary (in words and in
figures), and the amounts of the salary components BASIC, HRA, MEDICAL and OTHER.

Setup:
- The salary components must exist with these exact codes: **BASIC** (basic salary), **HRA** (housing allowance),
  **MEDICAL** (medical allowance) and **OTHER** (other allowance). Add **OTHER** if you do not have it yet.
- Set the employee's **gender** (for he/she and his/her).
- The employee needs a salary.

## Field reference

| Field | Gives |
|-------|-------|
| `{Employee.Name}` | Employee's name |
| `{Employee.Title}` | Mr. or Ms. (from gender) |
| `{Employee.HeShe}`, `{Employee.HisHer}`, `{Employee.HimHer}` | he/she, his/her, him/her (capitalised at the start of a sentence) |
| `{Employee.SonDaughter:cap}` | Son or Daughter |
| `{Employee.FatherName}` | Father's name |
| `{Employee.Designation}` | Designation |
| `{Employee.Responsibilities}` | Designation description, one line per bullet |
| `{Employee.JoinDate:MMMM yyyy}` | e.g. June 2023 (any date pattern works: `d MMMM yyyy`, `MMMM d, yyyy`, ...) |
| `{Employee.JoinDate:%d}{Employee.JoinDate:ord}` | Day and its suffix, e.g. 1 and st (make `ord` superscript in Word) |
| `{Employee.ExitDate:...}`, `{Letter.Date:...}` | Same date patterns as above |
| `{Salary.Gross:n0}` | e.g. 70,000 |
| `{Salary.Gross:words}` | e.g. Seventy Thousand |
| `{Salary.BASIC:n0}` (and HRA, MEDICAL, OTHER) | Amount of that salary component |

To rebuild these files from the originals in `doc-files/`, run `python -I tools/dev/letters/convert_samples.py`.
