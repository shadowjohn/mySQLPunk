import os

root_dir = r"D:\mytools\mySQLPunk\mySQLPunk"
replacements = {
    "mySQLPunk": "mySQLPunk",
    "mySQLPunk_main": "mySQLPunk_main",
    "mySQLPunk": "mySQLPunk",
    "羽山的 mySQLPunk": "mySQLPunk"
}

for root, dirs, files in os.walk(root_dir):
    for file in files:
        if file.endswith((".cs", ".csproj", ".sln", ".resx", ".Designer.cs")):
            file_path = os.path.join(root, file)
            try:
                with open(file_path, "r", encoding="utf-8") as f:
                    content = f.read()
                
                new_content = content
                for old, new in replacements.items():
                    new_content = new_content.replace(old, new)
                
                if new_content != content:
                    with open(file_path, "w", encoding="utf-8") as f:
                        f.write(new_content)
                    print(f"Updated: {file}")
            except Exception as e:
                print(f"Error processing {file}: {e}")
