package me.vbu.mirrordirectories.service;

import me.vbu.mirrordirectories.model.DirectoryNode;
import me.vbu.mirrordirectories.model.FileNode;
import me.vbu.mirrordirectories.model.Node;

import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.List;

/**
 * Service for comparing directories using a hierarchical approach.
 * Creates a tree structure representing the differences between directories.
 */
public class DirectoryComparator {


    // Instance variables for source and destination directories
    private File sourceDir;
    private File destDir;

    // Root node containing the comparison result
    private DirectoryNode comparisonResult;

    /**
     * Singleton instance
     */
    private static DirectoryComparator instance;

    /**
     * Private constructor to enforce singleton pattern.
     */
    private DirectoryComparator() {
        // Private constructor
    }

    /**
     * Gets the singleton instance.
     *
     * @return The singleton instance
     */
    public static synchronized DirectoryComparator getInstance() {
        if (instance == null) {
            instance = new DirectoryComparator();
        }
        return instance;
    }

    /**
     * Recursively compares directories and returns a hierarchy of paths that exist in
     * source but not in destination.
     *
     * @param sourceDir Source directory
     * @param destDir Destination directory
     * @return Root node of the missing items hierarchy
     */
    public DirectoryNode compareDirectories(File sourceDir, File destDir) {
        this.sourceDir = sourceDir;
        this.destDir = destDir;
        String dirName = sourceDir.getName();
        comparisonResult = new DirectoryNode(dirName);
        compareDirectoriesInternal(sourceDir, destDir, comparisonResult);
        return comparisonResult;
    }

    /**
     * Internal method to recursively compare directories and build the hierarchy.
     *
     * @param sourceDir Source directory
     * @param destDir Destination directory
     * @param parentNode Parent node to add missing items to
     */
    private void compareDirectoriesInternal(File sourceDir, File destDir, DirectoryNode parentNode) {
        File[] sourceContents = sourceDir.listFiles();

        if (sourceContents != null) {
            for (File sourceItem : sourceContents) {
                File destItem = new File(destDir, sourceItem.getName());

                if (!destItem.exists()) {
                    // Item doesn't exist in destination
                    Node newNode;
                    if (sourceItem.isDirectory()) {
                        newNode = new DirectoryNode(sourceItem.getName());
                        // If it's a directory, recursively add all its contents
                        addAllContents(sourceItem, (DirectoryNode) newNode);
                    } else {
                        newNode = new FileNode(sourceItem.getName());
                    }
                    parentNode.addChild(newNode);
                } else if (sourceItem.isDirectory() && destItem.isDirectory()) {
                    // Both are directories, check if there's any difference inside
                    DirectoryNode dirNode = new DirectoryNode(sourceItem.getName());
                    compareDirectoriesInternal(sourceItem, destItem, dirNode);

                    // Only add the directory if it has missing children
                    if (dirNode.hasChildren()) {
                        parentNode.addChild(dirNode);
                    }
                }
            }
        }
    }

    /**
     * Recursively adds all contents of a directory to the node hierarchy.
     *
     * @param dir Directory to add
     * @param parentNode Parent node to add contents to
     */
    private void addAllContents(File dir, DirectoryNode parentNode) {
        File[] contents = dir.listFiles();

        if (contents != null) {
            for (File item : contents) {
                Node node;
                if (item.isDirectory()) {
                    node = new DirectoryNode(item.getName());
                    addAllContents(item, (DirectoryNode) node);
                } else {
                    node = new FileNode(item.getName());
                }
                parentNode.addChild(node);
            }
        }
    }


    /**
     * Copies all differences from source to destination using the FileNode structure.
     *
     * @return Number of successfully copied items
     * @throws IOException If an I/O error occurs during file operations
     */
    public int copyMissingItems() throws IOException {
        if (comparisonResult == null) {
            throw new IllegalStateException("No comparison has been performed yet. Call compareDirectories() first.");
        }

        return copyMissingItems(comparisonResult);
    }

    /**
     * Copies all differences from source to destination using a specific Node structure.
     *
     * @param rootNode The root node of the differences hierarchy
     * @return Number of successfully copied items
     * @throws IOException If an I/O error occurs during file operations
     */
    public int copyMissingItems(Node rootNode) throws IOException {
        if (rootNode == null) {
            return 0;
        }

        return copyNodeContents(rootNode, sourceDir, destDir, "");
    }

    /**
     * Recursively copies a node and its children from source to destination.
     *
     * @param node The node to copy
     * @param sourceRoot Source root directory
     * @param destRoot Destination root directory
     * @param relativePath Current relative path
     * @return Number of successfully copied items
     * @throws IOException If an I/O error occurs during file operations
     */
    private int copyNodeContents(Node node, File sourceRoot, File destRoot, String relativePath) throws IOException {
        int copiedCount = 0;

//        // Build the current relative path
//        String currentPath = relativePath.isEmpty() ?
//                node.getName() : relativePath + File.separator + node.getName();

        // Get the source and destination files
        File sourceFile = new File(sourceRoot, relativePath);
        File destFile = new File(destRoot, relativePath);

        // Create parent directories if needed
        if (!ensureParentDirectoryExists(destFile)) {
            return 0;
        }

        if (node.isDirectory()) {
            // Create the directory
            if (destFile.exists() || destFile.mkdir()) {
                System.out.println("Directory created or already exists: " + relativePath);
                copiedCount++;

                // Copy all children
                DirectoryNode dirNode = (DirectoryNode) node;
                for (Node child : dirNode.getChildren().values()) {
                    copiedCount += copyNodeContents(child, sourceRoot, destRoot, relativePath + File.separator + child.getName());
                }
            } else {
                System.err.println("Failed to create directory: " + relativePath);
            }
        } else {
            // Copy the file
            try {
                Path sourcePath = sourceFile.toPath();
                Path destPath = destFile.toPath();
                Files.copy(sourcePath, destPath, StandardCopyOption.REPLACE_EXISTING);
                System.out.println("Copied file: " + relativePath);
                copiedCount++;
            } catch (IOException e) {
                System.err.println("Failed to copy file: " + relativePath + " (" + e.getMessage() + ")");
            }
        }

        return copiedCount;
    }

    /**
     * Ensures the parent directory for a file exists
     *
     * @param file The file whose parent directory should exist
     * @return True if parent exists or was created, false otherwise
     */
    private boolean ensureParentDirectoryExists(File file) {
        if (file.getParentFile() != null && !file.getParentFile().exists()) {
            if (!file.getParentFile().mkdirs()) {
                System.err.println("Failed to create parent directories for: " + file.getPath());
                return false;
            }
        }
        return true;
    }
}
