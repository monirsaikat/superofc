# HR letters

Write a letter once, then print it for any employee without typing their details again. The app fills in names, designations, dates and salaries from each person's record.

## Letter templates

Go to **Settings › Letter templates**. Setup adds seven standard letters: appointment, confirmation, increment, salary certificate, experience certificate, relieving and warning. If you deleted them, **Add standard letters** puts back the missing ones.

To change a letter or write a new one:

1. Choose a template on the left, or click **New template**.
2. Type the subject and the letter. Where the employee's details should go, double-click a field in the **Fields** list on the right. It goes where your cursor is, for example `{Employee.Name}`.
3. Check the **Preview** at the bottom. It shows the letter for the employee picked next to it. Fields that are empty for that person are listed in red.
4. Click **Save**.

Writing tips:

- Leave an empty line between paragraphs.
- Put two stars around words to make them bold: `**with effect from {Salary.EffectiveFrom}**`.
- `{Employee.HeShe}`, `{Employee.HimHer}` and `{Employee.HisHer}` follow the employee's gender. At the start of a sentence they get a capital letter.
- `{Salary.GrossInWords}` writes the amount in words.
- `{Input.Reason}` (or any other name after `Input.`) is a box you fill in when you issue the letter. Use it for things the record does not hold, such as the reason for a warning.
- The **number prefix** starts each letter number: `HR` gives `HR/2026/0001`, `HR/2026/0002`, and so on.
- Untick **Show this template when issuing letters** to hide a template without deleting it.

Only HR and Admin can change templates. Letters that use salary fields can be issued only by people who can see salaries.

## Word templates: your own letters, exactly

If your company already has its letters in Word, use those files as templates. SuperOfc fills in each person's
details and changes nothing else: fonts, bold, superscript dates ("1<sup>st</sup> June"), tables, bullets, headers,
footers and the space left for printed letterhead stay exactly as in your file.

**Make a Word template**

1. Open your letter in Word. Replace each personal detail with a field in braces, for example
   `{Employee.Name}`, `{Employee.Designation}` or `{Salary.Gross:n0}`. The field takes the formatting of its first
   letter, so a bold name stays bold.
2. Save it as a Word Document (.docx).
3. In **Settings › Letter templates**, click **Add Word template** and choose the file.

SuperOfc checks the file first. It refuses macro-enabled files, password-protected files, files that link to other
files, and fields it does not know (the message lists them).

The standard **Salary certificate**, **Experience certificate**, **NOC letter** and **Certificate of employment** are
already Word templates, made from our own letters. Before issuing them, fill in **Settings › Company › Letters are
signed by** and the signatory's title, and check the company name there: the letters print exactly what you enter.
The setup each letter needs (father's name, responsibilities, salary components) is listed in `doc-files/templates`.

**Formats for dates and amounts**

Add a format after a colon:

| You write | You get |
|-----------|---------|
| `{Employee.JoinDate:d MMMM yyyy}` | 1 June 2023 |
| `{Employee.JoinDate:MMMM d, yyyy}` | June 1, 2023 |
| `{Employee.JoinDate:%d}{Employee.JoinDate:ord}` | 1 and st; make the second field superscript in Word for "1<sup>st</sup>" |
| `{Salary.Gross:n0}` | 70,000 |
| `{Salary.Gross:words}` | Seventy Thousand |
| `{Employee.SonDaughter:cap}` | Son or Daughter |
| `{Employee.Name:upper}` | MD MASUD ALAM |

Other useful fields: `{Employee.FatherName}` (enter it on the employee's profile), `{Employee.Responsibilities}`
(the designation's description, one line per bullet: put the field alone in a bulleted line) and
`{Company.CurrencyCode}` (BDT).

**Change a Word template**

Select it and click **Edit in Word**. Make your changes in Word, save, then click **Use saved changes**.
**Open sample** shows the letter filled in for the sample employee. **Replace Word file** uploads a different file;
**Remove Word file** turns it back into a text template.

**Issue Word letters**

Issue them like other letters. Under **Save as**, choose **PDF** (made by Microsoft Word, so it looks exactly like the
Word file; Word must be installed on this PC) or **Word document**. Letters for several people are saved one file per
person in the folder you choose. The register keeps each filled file, so **Reprint** gives exactly the same letter.

## Issuing letters

Go to **Letters** in the menu, or open an employee and click **Letters › New letter**.

1. On the left, tick the people. Use the department list, the search box and **Select all** to pick many people at once.
2. Choose the template and the date. Fill any boxes the template asks for.
3. Check the preview. It shows the letter for the first person ticked.
4. Tick **Save a separate PDF for each person** if you want one file each. Otherwise all letters go into one PDF, one letter per page. For a Word template, choose **PDF** or **Word document** under **Save as**; letters for several people are always one file each.
5. Click **Issue and save PDF**.

If any person is missing a detail the letter needs, such as a designation or department, nothing is printed. The message lists each person and what is missing. Fill those details in the employee record, then issue again.

## Issued letters and reprints

The **Issued letters** tab lists every letter with its number, date, person, template and who issued it. Search by number, name or template. **Reprint** saves the letter exactly as it was issued, even if the template or the person's salary has changed since.

The employee page has a **Letters** tab with that person's letters.
