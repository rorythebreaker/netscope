// Acceptance check for TZ-NetScope-2 §5.5, run by analyzeHeadless after a NetScope annotation script.
// Argument: a TSV file "rva<TAB>expected name" built from `netscope --native --format json`.
// Prints one NSVERIFY line that tests/Ghidra/Check-Ghidra.ps1 parses.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.List;

public class NsVerify extends GhidraScript {
    @Override
    public void run() throws Exception {
        List<String> lines = Files.readAllLines(Paths.get(getScriptArgs()[0]), StandardCharsets.UTF_8);
        Address base = currentProgram.getImageBase();
        int ok = 0, missing = 0, wrong = 0;
        for (String line : lines) {
            if (line.isEmpty()) continue;
            String[] p = line.split("\t", 2);
            Address addr = base.add(Long.parseLong(p[0]));
            Function f = getFunctionAt(addr);
            if (f == null) {
                if (++missing <= 5) println("MISSING " + addr + " " + p[1]);
            } else if (!f.getName().equals(p[1])) {
                if (++wrong <= 5) println("WRONG   " + addr + " expected=" + p[1] + " actual=" + f.getName());
            } else {
                ok++;
            }
        }
        // For the idempotency check: a second run of the same script must not add symbols or bookmarks.
        int userSymbols = 0;
        SymbolIterator it = currentProgram.getSymbolTable().getAllSymbols(true);
        while (it.hasNext()) {
            Symbol s = it.next();
            if (s.getSource() == SourceType.USER_DEFINED) userSymbols++;
        }
        int bookmarks = currentProgram.getBookmarkManager().getBookmarkCount();
        println("NSVERIFY expected=" + (ok + missing + wrong) + " ok=" + ok + " missing=" + missing + " wrong=" + wrong
            + " userSymbols=" + userSymbols + " bookmarks=" + bookmarks);
    }
}
