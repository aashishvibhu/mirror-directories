package me.vbu.mirrordirectories.service;

import java.io.IOException;
import java.nio.file.Path;

/**
 * Interface for file operations like copy, move, etc.
 * Follows the Strategy pattern to allow easy extension with new operations.
 */
public interface FileOperation {
    /**
     * Performs the operation on a file
     *
     * @param sourcePath Source file path
     * @param destPath Destination file path
     * @return true if operation successful, false otherwise
     * @throws IOException If an I/O error occurs
     */
    boolean executeFileOperation(Path sourcePath, Path destPath) throws IOException;

    /**
     * Gets the name of this operation (e.g., "Copy", "Move")
     *
     * @return The operation name
     */
    String getOperationName();
}
