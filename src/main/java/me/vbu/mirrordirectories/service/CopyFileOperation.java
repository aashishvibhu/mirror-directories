package me.vbu.mirrordirectories.service;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;

/**
 * Implementation of FileOperation for copy operations.
 */
public class CopyFileOperation implements FileOperation {
    @Override
    public boolean executeFileOperation(Path sourcePath, Path destPath) throws IOException {
        Files.copy(sourcePath, destPath, StandardCopyOption.REPLACE_EXISTING);
        return true;
    }

    @Override
    public String getOperationName() {
        return "Copy";
    }
}
